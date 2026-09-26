package main

import (
	"archive/zip"
	"context"
	"crypto/subtle"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"time"

	"tailscale.com/tsnet"
)

type commonFlags struct {
	stateDir string
	hostname string
}

type stringList []string

func (s *stringList) String() string { return strings.Join(*s, ",") }
func (s *stringList) Set(value string) error {
	*s = append(*s, value)
	return nil
}

func addCommon(fs *flag.FlagSet) *commonFlags {
	c := &commonFlags{}
	fs.StringVar(&c.stateDir, "state", "", "persistent tsnet state directory")
	fs.StringVar(&c.hostname, "hostname", "", "tailnet node hostname")
	return c
}

func server(c *commonFlags) (*tsnet.Server, error) {
	if strings.TrimSpace(c.stateDir) == "" {
		return nil, errors.New("--state is required")
	}
	if strings.TrimSpace(c.hostname) == "" {
		return nil, errors.New("--hostname is required")
	}
	if err := os.MkdirAll(c.stateDir, 0o700); err != nil {
		return nil, err
	}
	return &tsnet.Server{
		Dir:      filepath.Clean(c.stateDir),
		Hostname: c.hostname,
		UserLogf: func(format string, args ...any) {
			fmt.Printf("[Bees tailnet] "+format+"\n", args...)
		},
	}, nil
}

func up(ctx context.Context, s *tsnet.Server) (string, error) {
	if _, err := s.Up(ctx); err != nil {
		return "", err
	}
	ip4, _ := s.TailscaleIPs()
	if !ip4.IsValid() {
		return "", errors.New("tailnet authentication completed without an IPv4 address")
	}
	return ip4.String(), nil
}

func runAuth(args []string) error {
	fs := flag.NewFlagSet("auth", flag.ContinueOnError)
	c := addCommon(fs)
	ipFile := fs.String("ip-file", "", "optional file to write this node's tailnet IPv4 address")
	if err := fs.Parse(args); err != nil {
		return err
	}
	s, err := server(c)
	if err != nil {
		return err
	}
	defer s.Close()

	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	ctx, timeoutCancel := context.WithTimeout(ctx, 10*time.Minute)
	defer timeoutCancel()

	ip4, err := up(ctx, s)
	if err != nil {
		return err
	}
	if strings.TrimSpace(*ipFile) != "" {
		if err := os.WriteFile(*ipFile, []byte(ip4+"\n"), 0o600); err != nil {
			return fmt.Errorf("write tailnet IPv4 file: %w", err)
		}
	}
	fmt.Printf("[Bees tailnet] authenticated hostname=%s tailscale_ip=%s\n", c.hostname, ip4)
	return nil
}

func copyConn(a, b net.Conn) {
	done := make(chan struct{}, 2)
	cp := func(dst, src net.Conn) {
		_, _ = io.Copy(dst, src)
		if tcp, ok := dst.(*net.TCPConn); ok {
			_ = tcp.CloseWrite()
		}
		done <- struct{}{}
	}
	go cp(a, b)
	go cp(b, a)
	<-done
	_ = a.Close()
	_ = b.Close()
	<-done
}

func proxyListener(
	ctx context.Context,
	ln net.Listener,
	dial func(context.Context) (net.Conn, error),
	label string,
	onFailure func(error),
) {
	for {
		src, err := ln.Accept()
		if err != nil {
			if ctx.Err() == nil {
				failure := fmt.Errorf("%s accept failed: %w", label, err)
				log.Printf("[Bees tailnet] %v", failure)
				if onFailure != nil {
					onFailure(failure)
				}
			}
			return
		}
		go func() {
			dialCtx, cancel := context.WithTimeout(ctx, 30*time.Second)
			defer cancel()
			dst, err := dial(dialCtx)
			if err != nil {
				log.Printf("[Bees tailnet] %s target unavailable: %v", label, err)
				_ = src.Close()
				return
			}
			copyConn(src, dst)
		}()
	}
}

func localDial(target string) func(context.Context) (net.Conn, error) {
	return func(ctx context.Context) (net.Conn, error) {
		var d net.Dialer
		return d.DialContext(ctx, "tcp", target)
	}
}

func tailnetDial(s *tsnet.Server, target string) func(context.Context) (net.Conn, error) {
	return func(ctx context.Context) (net.Conn, error) {
		return s.Dial(ctx, "tcp", target)
	}
}

func loadSecret(path string) (string, error) {
	if strings.TrimSpace(path) == "" {
		return "", errors.New("secret file path is required")
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	value := strings.TrimSpace(string(data))
	if value == "" {
		return "", fmt.Errorf("secret file is empty: %s", path)
	}
	return value, nil
}

func bearerMatches(header, expected string) bool {
	const prefix = "Bearer "
	if !strings.HasPrefix(header, prefix) {
		return false
	}
	actual := strings.TrimSpace(strings.TrimPrefix(header, prefix))
	if len(actual) != len(expected) {
		return false
	}
	return subtle.ConstantTimeCompare([]byte(actual), []byte(expected)) == 1
}

func snapshotRegularFile(path string) (*os.File, int64, func(), error) {
	info, err := os.Stat(path)
	if err != nil {
		return nil, 0, nil, err
	}
	if !info.Mode().IsRegular() {
		return nil, 0, nil, fmt.Errorf("bootstrap source is not a regular file: %s", path)
	}

	// Copy the atomic publication file to a private snapshot before replying. On Windows this
	// closes the mutable bundle quickly, so the operator can publish the next generation even
	// while a slow worker is still downloading the previous complete snapshot.
	source, err := os.Open(path)
	if err != nil {
		return nil, 0, nil, err
	}
	snapshot, err := os.CreateTemp("", "bees-bootstrap-snapshot-*")
	if err != nil {
		_ = source.Close()
		return nil, 0, nil, err
	}
	snapshotPath := snapshot.Name()
	cleanup := func() {
		_ = snapshot.Close()
		_ = os.Remove(snapshotPath)
	}
	if _, err = io.Copy(snapshot, source); err != nil {
		_ = source.Close()
		cleanup()
		return nil, 0, nil, err
	}
	if err = source.Close(); err != nil {
		cleanup()
		return nil, 0, nil, err
	}
	snapshotInfo, err := snapshot.Stat()
	if err != nil {
		cleanup()
		return nil, 0, nil, err
	}
	if _, err = snapshot.Seek(0, io.SeekStart); err != nil {
		cleanup()
		return nil, 0, nil, err
	}
	return snapshot, snapshotInfo.Size(), cleanup, nil
}

func bootstrapHandler(token, bundlePath string) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet || r.URL.Path != "/bootstrap" {
			http.NotFound(w, r)
			return
		}
		if !bearerMatches(r.Header.Get("Authorization"), token) {
			w.Header().Set("WWW-Authenticate", "Bearer")
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}

		snapshot, size, cleanup, err := snapshotRegularFile(bundlePath)
		if err != nil {
			http.Error(w, "bootstrap payload is not ready", http.StatusServiceUnavailable)
			return
		}
		defer cleanup()

		w.Header().Set("Content-Type", "application/zip")
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("Content-Disposition", "attachment; filename=bees-bootstrap.zip")
		w.Header().Set("Content-Length", strconv.FormatInt(size, 10))
		if _, err := io.Copy(w, snapshot); err != nil {
			log.Printf("[Bees tailnet] bootstrap snapshot write failed: %v", err)
		}
	})
}

func parsePort(value string) (int, error) {
	port, err := strconv.Atoi(value)
	if err != nil || port < 1 || port > 65535 {
		return 0, fmt.Errorf("invalid port %q", value)
	}
	return port, nil
}

func writeGatewayHealth(path, ip4 string, controlPort, brokerPort, bootstrapPort int) error {
	if strings.TrimSpace(path) == "" {
		return nil
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	now := time.Now()
	if _, err := os.Stat(path); errors.Is(err, os.ErrNotExist) {
		payload := fmt.Sprintf(
			"ready ip=%s control=%d broker=%d bootstrap=%d\n",
			ip4,
			controlPort,
			brokerPort,
			bootstrapPort,
		)
		if err := os.WriteFile(path, []byte(payload), 0o600); err != nil {
			return err
		}
	}
	return os.Chtimes(path, now, now)
}

func serveGatewaySession(
	ctx context.Context,
	c *commonFlags,
	controlPort int,
	brokerPort int,
	bootstrapPort int,
	bootstrapBundlePath string,
	token string,
	healthFile string,
) error {
	s, err := server(c)
	if err != nil {
		return err
	}
	defer s.Close()

	ip4, err := up(ctx, s)
	if err != nil {
		return err
	}

	controlLn, err := s.Listen("tcp", fmt.Sprintf(":%d", controlPort))
	if err != nil {
		return fmt.Errorf("listen control: %w", err)
	}
	defer controlLn.Close()
	brokerLn, err := s.Listen("tcp", fmt.Sprintf(":%d", brokerPort))
	if err != nil {
		return fmt.Errorf("listen broker: %w", err)
	}
	defer brokerLn.Close()
	bootstrapLn, err := s.Listen("tcp", fmt.Sprintf(":%d", bootstrapPort))
	if err != nil {
		return fmt.Errorf("listen bootstrap: %w", err)
	}
	defer bootstrapLn.Close()

	sessionCtx, sessionCancel := context.WithCancel(ctx)
	defer sessionCancel()
	failures := make(chan error, 1)
	fail := func(failure error) {
		if failure == nil {
			return
		}
		select {
		case failures <- failure:
		default:
		}
		sessionCancel()
	}

	go proxyListener(
		sessionCtx,
		controlLn,
		localDial(fmt.Sprintf("127.0.0.1:%d", controlPort)),
		"control",
		fail,
	)
	go proxyListener(
		sessionCtx,
		brokerLn,
		localDial(fmt.Sprintf("127.0.0.1:%d", brokerPort)),
		"broker",
		fail,
	)

	httpServer := &http.Server{Handler: bootstrapHandler(token, bootstrapBundlePath)}
	go func() {
		if err := httpServer.Serve(bootstrapLn); err != nil &&
			!errors.Is(err, http.ErrServerClosed) &&
			sessionCtx.Err() == nil {
			fail(fmt.Errorf("bootstrap HTTP server failed: %w", err))
		}
	}()
	go func() {
		<-sessionCtx.Done()
		shutdownCtx, shutdownCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer shutdownCancel()
		_ = httpServer.Shutdown(shutdownCtx)
		_ = controlLn.Close()
		_ = brokerLn.Close()
		_ = bootstrapLn.Close()
	}()

	if strings.TrimSpace(healthFile) != "" {
		_ = os.Remove(healthFile)
		defer os.Remove(healthFile)
		if err := writeGatewayHealth(
			healthFile,
			ip4,
			controlPort,
			brokerPort,
			bootstrapPort,
		); err != nil {
			return fmt.Errorf("write gateway health: %w", err)
		}
		go func() {
			ticker := time.NewTicker(2 * time.Second)
			defer ticker.Stop()
			for {
				select {
				case <-sessionCtx.Done():
					return
				case <-ticker.C:
					if err := writeGatewayHealth(
						healthFile,
						ip4,
						controlPort,
						brokerPort,
						bootstrapPort,
					); err != nil {
						fail(fmt.Errorf("refresh gateway health: %w", err))
						return
					}
				}
			}
		}()
	}

	log.Printf(
		"[Bees tailnet] gateway online ip=%s control=%d broker=%d bootstrap=%d",
		ip4, controlPort, brokerPort, bootstrapPort,
	)
	select {
	case <-ctx.Done():
		return nil
	case failure := <-failures:
		return failure
	}
}

func runGateway(args []string) error {
	fs := flag.NewFlagSet("gateway", flag.ContinueOnError)
	c := addCommon(fs)
	controlPort := fs.Int("control-port", 7150, "tailnet port proxying learner control")
	brokerPort := fs.Int("broker-port", 55051, "tailnet port proxying WAN broker")
	bootstrapPort := fs.Int("bootstrap-port", 7151, "tailnet bootstrap port")
	bootstrapBundlePath := fs.String("bootstrap-bundle", "", "atomic remote bootstrap bundle path")
	bootstrapTokenPath := fs.String("bootstrap-token", "", "bootstrap bearer token file")
	healthFile := fs.String("health-file", "", "optional gateway liveness heartbeat file")
	_ = fs.String("owner-token", "", "opaque managed-launch ownership token")
	if err := fs.Parse(args); err != nil {
		return err
	}
	for _, port := range []int{*controlPort, *brokerPort, *bootstrapPort} {
		if port < 1 || port > 65535 {
			return errors.New("gateway ports must be in 1-65535")
		}
	}
	if *controlPort == *brokerPort || *controlPort == *bootstrapPort || *brokerPort == *bootstrapPort {
		return errors.New("control, broker, and bootstrap ports must be distinct")
	}
	token, err := loadSecret(*bootstrapTokenPath)
	if err != nil {
		return fmt.Errorf("bootstrap token: %w", err)
	}
	if strings.TrimSpace(*bootstrapBundlePath) == "" {
		return errors.New("--bootstrap-bundle is required")
	}
	if info, err := os.Stat(*bootstrapBundlePath); err != nil || !info.Mode().IsRegular() {
		if err != nil {
			return fmt.Errorf("bootstrap bundle: %w", err)
		}
		return fmt.Errorf("bootstrap bundle is not a regular file: %s", *bootstrapBundlePath)
	}

	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	for {
		err := serveGatewaySession(
			ctx,
			c,
			*controlPort,
			*brokerPort,
			*bootstrapPort,
			*bootstrapBundlePath,
			token,
			*healthFile,
		)
		if ctx.Err() != nil {
			return nil
		}
		if err != nil {
			log.Printf("[Bees tailnet] gateway session failed: %v; restarting", err)
		}
		select {
		case <-ctx.Done():
			return nil
		case <-time.After(2 * time.Second):
		}
	}
}

func atomicWrite(path string, mode os.FileMode, write func(io.Writer) error) error {
	if strings.TrimSpace(path) == "" {
		return errors.New("output path is required")
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	temp := path + ".tmp"
	_ = os.Remove(temp)
	file, err := os.OpenFile(temp, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, mode)
	if err != nil {
		return err
	}
	ok := false
	defer func() {
		_ = file.Close()
		if !ok {
			_ = os.Remove(temp)
		}
	}()
	if err := write(file); err != nil {
		return err
	}
	if err := file.Close(); err != nil {
		return err
	}
	if err := os.Chmod(temp, mode); err != nil {
		return err
	}
	if err := os.Remove(path); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	if err := os.Rename(temp, path); err != nil {
		return err
	}
	ok = true
	return nil
}

func extractBootstrap(path, runtimeOut, workerTokenOut, wanTokenOut string) error {
	reader, err := zip.OpenReader(path)
	if err != nil {
		return err
	}
	defer reader.Close()

	outputs := map[string]struct {
		path string
		mode os.FileMode
	}{
		"bees-remote-runtime.zip": {runtimeOut, 0o644},
		"training-worker.token":   {workerTokenOut, 0o600},
		"wan.token":               {wanTokenOut, 0o600},
	}
	seen := map[string]bool{}
	for _, entry := range reader.File {
		spec, ok := outputs[entry.Name]
		if !ok || seen[entry.Name] {
			continue
		}
		if entry.FileInfo().IsDir() {
			return fmt.Errorf("invalid bootstrap directory entry %s", entry.Name)
		}
		src, err := entry.Open()
		if err != nil {
			return err
		}
		err = atomicWrite(spec.path, spec.mode, func(dst io.Writer) error {
			_, copyErr := io.Copy(dst, src)
			return copyErr
		})
		_ = src.Close()
		if err != nil {
			return err
		}
		seen[entry.Name] = true
	}
	for name := range outputs {
		if !seen[name] {
			return fmt.Errorf("bootstrap archive is missing %s", name)
		}
	}
	return nil
}

func runProbe(args []string) error {
	fs := flag.NewFlagSet("probe", flag.ContinueOnError)
	c := addCommon(fs)
	target := fs.String("target", "", "tailnet target host:port")
	timeout := fs.Duration("timeout", 10*time.Second, "probe timeout")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if strings.TrimSpace(*target) == "" {
		return errors.New("--target is required")
	}
	if *timeout <= 0 {
		return errors.New("--timeout must be positive")
	}

	s, err := server(c)
	if err != nil {
		return err
	}
	defer s.Close()

	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	ctx, timeoutCancel := context.WithTimeout(ctx, *timeout)
	defer timeoutCancel()

	ip4, err := up(ctx, s)
	if err != nil {
		return err
	}
	conn, err := s.Dial(ctx, "tcp", *target)
	if err != nil {
		return fmt.Errorf(
			"tailnet probe from %s to %s failed: %w; verify both Bees nodes were authorized into the same Tailscale tailnet and that the learner gateway is running",
			ip4,
			*target,
			err,
		)
	}
	_ = conn.Close()
	fmt.Printf("[Bees tailnet] probe succeeded source=%s target=%s\n", ip4, *target)
	return nil
}

func runFetch(args []string) error {
	fs := flag.NewFlagSet("fetch", flag.ContinueOnError)
	c := addCommon(fs)
	target := fs.String("target", "", "learner tailnet bootstrap target host:port")
	tokenFile := fs.String("token-file", "", "bootstrap bearer token file")
	runtimeOut := fs.String("runtime-out", "", "destination runtime zip")
	workerTokenOut := fs.String("worker-token-out", "", "destination worker token")
	wanTokenOut := fs.String("wan-token-out", "", "destination WAN token")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if strings.TrimSpace(*target) == "" {
		return errors.New("--target is required")
	}
	token, err := loadSecret(*tokenFile)
	if err != nil {
		return fmt.Errorf("bootstrap token: %w", err)
	}
	s, err := server(c)
	if err != nil {
		return err
	}
	defer s.Close()

	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	ip4, err := up(ctx, s)
	if err != nil {
		return err
	}

	// Keep reachability validation and the HTTP bootstrap request on the same tsnet.Server.
	// Repeatedly tearing down and recreating the same tsnet identity can briefly leave the
	// replacement endpoint unable to dial even though the preceding instance was reachable.
	var lastDialErr error
	for attempt := 1; attempt <= 3; attempt++ {
		dialCtx, dialCancel := context.WithTimeout(ctx, 10*time.Second)
		conn, dialErr := s.Dial(dialCtx, "tcp", *target)
		dialCancel()
		if dialErr == nil {
			_ = conn.Close()
			fmt.Printf("[Bees tailnet] bootstrap reachability ready source=%s target=%s\n", ip4, *target)
			lastDialErr = nil
			break
		}
		lastDialErr = dialErr
		fmt.Printf("[Bees tailnet] bootstrap reachability attempt %d/3 failed: %v\n", attempt, dialErr)
		if attempt < 3 {
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-time.After(2 * time.Second):
			}
		}
	}
	if lastDialErr != nil {
		return fmt.Errorf(
			"bootstrap target %s is unreachable from %s after retries: %w",
			*target,
			ip4,
			lastDialErr,
		)
	}

	transport := &http.Transport{
		DialContext: func(ctx context.Context, network, address string) (net.Conn, error) {
			return s.Dial(ctx, network, address)
		},
	}
	defer transport.CloseIdleConnections()
	client := &http.Client{Transport: transport, Timeout: 5 * time.Minute}

	var resp *http.Response
	var requestErr error
	for attempt := 1; attempt <= 3; attempt++ {
		req, reqErr := http.NewRequestWithContext(ctx, http.MethodGet, "http://"+*target+"/bootstrap", nil)
		if reqErr != nil {
			return reqErr
		}
		req.Header.Set("Authorization", "Bearer "+token)
		resp, requestErr = client.Do(req)
		if requestErr == nil {
			break
		}
		fmt.Printf("[Bees tailnet] bootstrap request attempt %d/3 failed: %v\n", attempt, requestErr)
		if attempt < 3 {
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-time.After(2 * time.Second):
			}
		}
	}
	if requestErr != nil {
		return fmt.Errorf("bootstrap request failed after retries: %w", requestErr)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		body, _ := io.ReadAll(io.LimitReader(resp.Body, 4096))
		return fmt.Errorf("bootstrap server returned %s: %s", resp.Status, strings.TrimSpace(string(body)))
	}

	tempDir, err := os.MkdirTemp("", "bees-bootstrap-*")
	if err != nil {
		return err
	}
	defer os.RemoveAll(tempDir)
	archivePath := filepath.Join(tempDir, "bootstrap.zip")
	if err := atomicWrite(archivePath, 0o600, func(dst io.Writer) error {
		_, copyErr := io.Copy(dst, resp.Body)
		return copyErr
	}); err != nil {
		return err
	}
	return extractBootstrap(archivePath, *runtimeOut, *workerTokenOut, *wanTokenOut)
}

func parseMapping(value string) (string, string, error) {
	left, right, ok := strings.Cut(value, "=")
	if !ok || strings.TrimSpace(left) == "" || strings.TrimSpace(right) == "" {
		return "", "", fmt.Errorf("invalid --map %q; expected local=tailnet-target", value)
	}
	return strings.TrimSpace(left), strings.TrimSpace(right), nil
}

func runForwardMulti(args []string) error {
	fs := flag.NewFlagSet("forward-multi", flag.ContinueOnError)
	c := addCommon(fs)
	var mappings stringList
	fs.Var(&mappings, "map", "repeatable local=tailnet-target TCP mapping")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if len(mappings) == 0 {
		return errors.New("at least one --map is required")
	}
	s, err := server(c)
	if err != nil {
		return err
	}
	defer s.Close()

	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	if _, err := up(ctx, s); err != nil {
		return err
	}

	listeners := make([]net.Listener, 0, len(mappings))
	for _, mapping := range mappings {
		local, remote, err := parseMapping(mapping)
		if err != nil {
			return err
		}
		ln, err := net.Listen("tcp", local)
		if err != nil {
			return fmt.Errorf("listen %s: %w", local, err)
		}
		listeners = append(listeners, ln)
		go proxyListener(ctx, ln, tailnetDial(s, remote), local+" -> "+remote, nil)
		log.Printf("[Bees tailnet] forwarding %s -> %s", local, remote)
	}
	defer func() {
		for _, ln := range listeners {
			_ = ln.Close()
		}
	}()
	go func() {
		<-ctx.Done()
		for _, ln := range listeners {
			_ = ln.Close()
		}
	}()
	<-ctx.Done()
	return nil
}

func usage() {
	fmt.Fprintln(os.Stderr, "Usage: bees-tailnet-bridge <auth|probe|gateway|fetch|forward-multi> [options]")
}

func main() {
	if len(os.Args) < 2 {
		usage()
		os.Exit(2)
	}
	var err error
	switch os.Args[1] {
	case "auth":
		err = runAuth(os.Args[2:])
	case "probe":
		err = runProbe(os.Args[2:])
	case "gateway":
		err = runGateway(os.Args[2:])
	case "fetch":
		err = runFetch(os.Args[2:])
	case "forward-multi":
		err = runForwardMulti(os.Args[2:])
	default:
		usage()
		os.Exit(2)
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, "error:", err)
		os.Exit(1)
	}
}
