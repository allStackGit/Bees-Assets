package main

import (
	"archive/zip"
	"context"
	"crypto/sha256"
	"crypto/subtle"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/netip"
	"os"
	"os/signal"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
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

func bootstrapContentETag(reader io.Reader) (string, error) {
	hash := sha256.New()
	if _, err := io.Copy(hash, reader); err != nil {
		return "", err
	}
	return fmt.Sprintf("\"sha256-%x\"", hash.Sum(nil)), nil
}

type bootstrapMetadataCache struct {
	mu      sync.Mutex
	info    os.FileInfo
	size    int64
	modTime time.Time
	etag    string
}

func (c *bootstrapMetadataCache) metadata(path string) (int64, string, error) {
	c.mu.Lock()
	defer c.mu.Unlock()

	source, err := os.Open(path)
	if err != nil {
		return 0, "", err
	}
	defer source.Close()
	info, err := source.Stat()
	if err != nil {
		return 0, "", err
	}
	if !info.Mode().IsRegular() {
		return 0, "", fmt.Errorf("bootstrap source is not a regular file: %s", path)
	}

	if c.info != nil &&
		os.SameFile(c.info, info) &&
		c.size == info.Size() &&
		c.modTime.Equal(info.ModTime()) &&
		c.etag != "" {
		return c.size, c.etag, nil
	}

	etag, err := bootstrapContentETag(source)
	if err != nil {
		return 0, "", err
	}
	c.info = info
	c.size = info.Size()
	c.modTime = info.ModTime()
	c.etag = etag
	return c.size, c.etag, nil
}

func snapshotRegularFile(path string) (*os.File, int64, string, func(), error) {
	// Copy the atomic publication file to a private snapshot before replying. On Windows this
	// closes the mutable bundle quickly, so the operator can publish the next generation even
	// while a slow worker is still downloading the previous complete snapshot.
	source, err := os.Open(path)
	if err != nil {
		return nil, 0, "", nil, err
	}
	sourceInfo, err := source.Stat()
	if err != nil {
		_ = source.Close()
		return nil, 0, "", nil, err
	}
	if !sourceInfo.Mode().IsRegular() {
		_ = source.Close()
		return nil, 0, "", nil, fmt.Errorf("bootstrap source is not a regular file: %s", path)
	}
	hash := sha256.New()
	snapshot, err := os.CreateTemp("", "bees-bootstrap-snapshot-*")
	if err != nil {
		_ = source.Close()
		return nil, 0, "", nil, err
	}
	snapshotPath := snapshot.Name()
	cleanup := func() {
		_ = snapshot.Close()
		_ = os.Remove(snapshotPath)
	}
	if _, err = io.Copy(io.MultiWriter(snapshot, hash), source); err != nil {
		_ = source.Close()
		cleanup()
		return nil, 0, "", nil, err
	}
	etag := fmt.Sprintf("\"sha256-%x\"", hash.Sum(nil))
	if err = source.Close(); err != nil {
		cleanup()
		return nil, 0, "", nil, err
	}
	snapshotInfo, err := snapshot.Stat()
	if err != nil {
		cleanup()
		return nil, 0, "", nil, err
	}
	if _, err = snapshot.Seek(0, io.SeekStart); err != nil {
		cleanup()
		return nil, 0, "", nil, err
	}
	return snapshot, snapshotInfo.Size(), etag, cleanup, nil
}

func bootstrapHandler(token, bundlePath string) http.Handler {
	metadataCache := &bootstrapMetadataCache{}
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if (r.Method != http.MethodGet && r.Method != http.MethodHead) || r.URL.Path != "/bootstrap" {
			http.NotFound(w, r)
			return
		}
		if !bearerMatches(r.Header.Get("Authorization"), token) {
			w.Header().Set("WWW-Authenticate", "Bearer")
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}

		if r.Method == http.MethodHead {
			size, etag, err := metadataCache.metadata(bundlePath)
			if err != nil {
				http.Error(w, "bootstrap payload is not ready", http.StatusServiceUnavailable)
				return
			}
			w.Header().Set("Content-Type", "application/zip")
			w.Header().Set("Cache-Control", "no-store")
			w.Header().Set("ETag", etag)
			w.Header().Set("Content-Length", strconv.FormatInt(size, 10))
			w.WriteHeader(http.StatusOK)
			return
		}

		snapshot, size, etag, cleanup, err := snapshotRegularFile(bundlePath)
		if err != nil {
			http.Error(w, "bootstrap payload is not ready", http.StatusServiceUnavailable)
			return
		}
		defer cleanup()

		w.Header().Set("Content-Type", "application/zip")
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("Content-Disposition", "attachment; filename=bees-bootstrap.zip")
		w.Header().Set("ETag", etag)
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

func validateTailnetBackendStatus(backendState string, ips []netip.Addr, expectedIP string) error {
	if backendState != "Running" {
		return fmt.Errorf("tailnet backend state is %q, expected Running", backendState)
	}
	expected, err := netip.ParseAddr(expectedIP)
	if err != nil {
		return fmt.Errorf("invalid expected tailnet IPv4 %q: %w", expectedIP, err)
	}
	for _, candidate := range ips {
		if candidate == expected {
			return nil
		}
	}
	return fmt.Errorf(
		"tailnet backend no longer owns expected IP %s (assigned=%v)",
		expectedIP,
		ips,
	)
}

func checkTailnetBackend(ctx context.Context, s *tsnet.Server, expectedIP string) error {
	client, err := s.LocalClient()
	if err != nil {
		return fmt.Errorf("open tsnet local client: %w", err)
	}
	statusCtx, cancel := context.WithTimeout(ctx, 3*time.Second)
	defer cancel()
	status, err := client.StatusWithoutPeers(statusCtx)
	if err != nil {
		return fmt.Errorf("read tsnet backend status: %w", err)
	}
	return validateTailnetBackendStatus(
		status.BackendState,
		status.TailscaleIPs,
		expectedIP,
	)
}

func writeGatewayHealth(path, ip4 string, controlPort, brokerPort, bootstrapPort, gameplayPort int) error {
	if strings.TrimSpace(path) == "" {
		return nil
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	now := time.Now()
	if _, err := os.Stat(path); errors.Is(err, os.ErrNotExist) {
		payload := fmt.Sprintf(
			"ready ip=%s control=%d broker=%d bootstrap=%d gameplay=%d\n",
			ip4,
			controlPort,
			brokerPort,
			bootstrapPort,
			gameplayPort,
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
	gameplayPort int,
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
	gameplayLn, err := s.Listen("tcp", fmt.Sprintf(":%d", gameplayPort))
	if err != nil {
		return fmt.Errorf("listen gameplay: %w", err)
	}
	defer gameplayLn.Close()

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
	go proxyListener(
		sessionCtx,
		gameplayLn,
		localDial(fmt.Sprintf("127.0.0.1:%d", gameplayPort)),
		"gameplay",
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
		_ = gameplayLn.Close()
	}()

	if strings.TrimSpace(healthFile) != "" {
		_ = os.Remove(healthFile)
		healthDone := make(chan struct{})
		defer func() {
			sessionCancel()
			<-healthDone
			_ = os.Remove(healthFile)
		}()
		if err := writeGatewayHealth(
			healthFile,
			ip4,
			controlPort,
			brokerPort,
			bootstrapPort,
			gameplayPort,
		); err != nil {
			return fmt.Errorf("write gateway health: %w", err)
		}
		go func() {
			defer close(healthDone)
			ticker := time.NewTicker(2 * time.Second)
			defer ticker.Stop()
			consecutiveBackendFailures := 0
			for {
				select {
				case <-sessionCtx.Done():
					return
				case <-ticker.C:
					if err := checkTailnetBackend(sessionCtx, s, ip4); err != nil {
						consecutiveBackendFailures++
						log.Printf(
							"[Bees tailnet] gateway backend health check failed (%d/3): %v",
							consecutiveBackendFailures,
							err,
						)
						if consecutiveBackendFailures >= 3 {
							fail(fmt.Errorf(
								"tailnet backend remained unhealthy across %d checks: %w",
								consecutiveBackendFailures,
								err,
							))
							return
						}
						// Do not refresh the health-file mtime while the embedded Tailscale
						// backend is unhealthy. This lets the outer operator independently
						// detect a stale gateway even before the child restart completes.
						continue
					}
					consecutiveBackendFailures = 0
					if err := writeGatewayHealth(
						healthFile,
						ip4,
						controlPort,
						brokerPort,
						bootstrapPort,
						gameplayPort,
					); err != nil {
						fail(fmt.Errorf("refresh gateway health: %w", err))
						return
					}
				}
			}
		}()
	}

	log.Printf(
		"[Bees tailnet] gateway online ip=%s control=%d broker=%d bootstrap=%d gameplay=%d",
		ip4, controlPort, brokerPort, bootstrapPort, gameplayPort,
	)
	select {
	case <-ctx.Done():
		return nil
	case failure := <-failures:
		return failure
	}
}

func managedFlagValue(args []string, name string) string {
	for index := 0; index < len(args); index++ {
		argument := args[index]
		if argument == name && index+1 < len(args) {
			return args[index+1]
		}
		if strings.HasPrefix(argument, name+"=") {
			return strings.TrimPrefix(argument, name+"=")
		}
	}
	return ""
}

func withoutManagedOwnerToken(args []string) []string {
	result := make([]string, 0, len(args))
	for index := 0; index < len(args); index++ {
		argument := args[index]
		if argument == "--owner-token" {
			if index+1 < len(args) {
				index++
			}
			continue
		}
		if strings.HasPrefix(argument, "--owner-token=") {
			continue
		}
		result = append(result, argument)
	}
	return result
}

func managedChildOwnerToken(ownerToken string) string {
	sum := sha256.Sum256([]byte("bees-managed-child:" + ownerToken))
	return fmt.Sprintf("%x", sum)
}

func runGatewaySupervisor(args []string) error {
	ownerToken := managedFlagValue(args, "--owner-token")
	childArgs := withoutManagedOwnerToken(args)
	if strings.TrimSpace(ownerToken) != "" {
		childArgs = append(childArgs, "--owner-token", managedChildOwnerToken(ownerToken))
	}
	healthFile := managedFlagValue(childArgs, "--health-file")
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()

	for {
		if ctx.Err() != nil {
			return nil
		}
		if strings.TrimSpace(healthFile) != "" {
			_ = os.Remove(healthFile)
		}
		commandArgs := append([]string{"gateway"}, childArgs...)
		child := exec.Command(os.Args[0], commandArgs...)
		child.Stdout = os.Stdout
		child.Stderr = os.Stderr
		child.Stdin = os.Stdin
		if err := child.Start(); err != nil {
			log.Printf("[Bees tailnet] gateway child launch failed: %v; retrying", err)
			select {
			case <-ctx.Done():
				return nil
			case <-time.After(2 * time.Second):
				continue
			}
		}
		log.Printf("[Bees tailnet] gateway supervisor started child pid=%d", child.Process.Pid)
		done := make(chan error, 1)
		go func() {
			done <- child.Wait()
		}()

		select {
		case <-ctx.Done():
			_ = child.Process.Kill()
			<-done
			if strings.TrimSpace(healthFile) != "" {
				_ = os.Remove(healthFile)
			}
			return nil
		case err := <-done:
			if strings.TrimSpace(healthFile) != "" {
				_ = os.Remove(healthFile)
			}
			if err != nil {
				log.Printf("[Bees tailnet] gateway child exited: %v; restarting", err)
			} else {
				log.Printf("[Bees tailnet] gateway child exited cleanly; restarting")
			}
		}

		select {
		case <-ctx.Done():
			return nil
		case <-time.After(2 * time.Second):
		}
	}
}

func runGateway(args []string) error {
	fs := flag.NewFlagSet("gateway", flag.ContinueOnError)
	c := addCommon(fs)
	controlPort := fs.Int("control-port", 7150, "tailnet port proxying learner control")
	brokerPort := fs.Int("broker-port", 55051, "tailnet port proxying WAN broker")
	bootstrapPort := fs.Int("bootstrap-port", 7151, "tailnet bootstrap port")
	gameplayPort := fs.Int("gameplay-port", 7146, "tailnet port proxying learner gameplay/settings server")
	bootstrapBundlePath := fs.String("bootstrap-bundle", "", "atomic remote bootstrap bundle path")
	bootstrapTokenPath := fs.String("bootstrap-token", "", "bootstrap bearer token file")
	healthFile := fs.String("health-file", "", "optional gateway liveness heartbeat file")
	_ = fs.String("owner-token", "", "opaque managed-launch ownership token")
	if err := fs.Parse(args); err != nil {
		return err
	}
	for _, port := range []int{*controlPort, *brokerPort, *bootstrapPort, *gameplayPort} {
		if port < 1 || port > 65535 {
			return errors.New("gateway ports must be in 1-65535")
		}
	}
	if *controlPort == *brokerPort ||
		*controlPort == *bootstrapPort ||
		*controlPort == *gameplayPort ||
		*brokerPort == *bootstrapPort ||
		*brokerPort == *gameplayPort ||
		*bootstrapPort == *gameplayPort {
		return errors.New("control, broker, bootstrap, and gameplay ports must be distinct")
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
			*gameplayPort,
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

func copyBootstrapWithProgress(dst io.Writer, src io.Reader, contentLength int64) (int64, error) {
	buf := make([]byte, 1024*1024)
	var total int64
	lastReport := time.Now()
	for {
		n, readErr := src.Read(buf)
		if n > 0 {
			written, writeErr := dst.Write(buf[:n])
			total += int64(written)
			if writeErr != nil {
				return total, writeErr
			}
			if written != n {
				return total, io.ErrShortWrite
			}
			if time.Since(lastReport) >= 5*time.Second {
				if contentLength > 0 {
					fmt.Printf(
						"[Bees tailnet] bootstrap download progress %.1f/%.1f MiB\n",
						float64(total)/(1024*1024),
						float64(contentLength)/(1024*1024),
					)
				} else {
					fmt.Printf(
						"[Bees tailnet] bootstrap download progress %.1f MiB\n",
						float64(total)/(1024*1024),
					)
				}
				lastReport = time.Now()
			}
		}
		if errors.Is(readErr, io.EOF) {
			return total, nil
		}
		if readErr != nil {
			return total, readErr
		}
	}
}

type idleTimeoutConn struct {
	net.Conn
	idle time.Duration
}

func (c *idleTimeoutConn) Read(p []byte) (int, error) {
	if c.idle > 0 {
		_ = c.Conn.SetReadDeadline(time.Now().Add(c.idle))
	}
	return c.Conn.Read(p)
}

func (c *idleTimeoutConn) Write(p []byte) (int, error) {
	if c.idle > 0 {
		_ = c.Conn.SetWriteDeadline(time.Now().Add(c.idle))
	}
	return c.Conn.Write(p)
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
			dialCtx, dialCancel := context.WithTimeout(ctx, 15*time.Second)
			defer dialCancel()
			conn, dialErr := s.Dial(dialCtx, network, address)
			if dialErr != nil {
				return nil, dialErr
			}
			return &idleTimeoutConn{Conn: conn, idle: 30 * time.Second}, nil
		},
		ResponseHeaderTimeout: 20 * time.Second,
	}
	defer transport.CloseIdleConnections()
	client := &http.Client{Transport: transport, Timeout: 5 * time.Minute}

	var resp *http.Response
	var requestErr error
	for attempt := 1; attempt <= 3; attempt++ {
		fmt.Printf("[Bees tailnet] bootstrap request attempt %d/3...\n", attempt)
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
	if resp.ContentLength > 0 {
		fmt.Printf(
			"[Bees tailnet] bootstrap response ready; downloading %.1f MiB...\n",
			float64(resp.ContentLength)/(1024*1024),
		)
	} else {
		fmt.Println("[Bees tailnet] bootstrap response ready; downloading...")
	}

	tempDir, err := os.MkdirTemp("", "bees-bootstrap-*")
	if err != nil {
		return err
	}
	defer os.RemoveAll(tempDir)
	archivePath := filepath.Join(tempDir, "bootstrap.zip")
	var downloaded int64
	if err := atomicWrite(archivePath, 0o600, func(dst io.Writer) error {
		var copyErr error
		downloaded, copyErr = copyBootstrapWithProgress(dst, resp.Body, resp.ContentLength)
		return copyErr
	}); err != nil {
		return err
	}
	fmt.Printf(
		"[Bees tailnet] bootstrap download complete %.1f MiB.\n",
		float64(downloaded)/(1024*1024),
	)
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
	ip4, err := up(ctx, s)
	if err != nil {
		return err
	}

	go func() {
		ticker := time.NewTicker(2 * time.Second)
		defer ticker.Stop()
		consecutiveFailures := 0
		for {
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
				if err := checkTailnetBackend(ctx, s, ip4); err != nil {
					consecutiveFailures++
					log.Printf(
						"[Bees tailnet] forward backend health check failed (%d/3): %v",
						consecutiveFailures,
						err,
					)
					if consecutiveFailures >= 3 {
						log.Printf(
							"[Bees tailnet] forward backend remained unhealthy; restarting transport",
						)
						cancel()
						return
					}
					continue
				}
				consecutiveFailures = 0
			}
		}
	}()

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
	fmt.Fprintln(os.Stderr, "Usage: bees-tailnet-bridge <auth|probe|gateway-supervisor|gateway|fetch|forward-multi> [options]")
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
	case "gateway-supervisor":
		err = runGatewaySupervisor(os.Args[2:])
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
