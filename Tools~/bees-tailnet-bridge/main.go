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

func proxyListener(ctx context.Context, ln net.Listener, dial func(context.Context) (net.Conn, error), label string) {
	for {
		src, err := ln.Accept()
		if err != nil {
			if ctx.Err() == nil {
				log.Printf("[Bees tailnet] %s accept failed: %v", label, err)
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

func zipFile(z *zip.Writer, archiveName, path string, mode os.FileMode) error {
	info, err := os.Stat(path)
	if err != nil {
		return err
	}
	if !info.Mode().IsRegular() {
		return fmt.Errorf("bootstrap source is not a regular file: %s", path)
	}
	header := &zip.FileHeader{
		Name:   archiveName,
		Method: zip.Store,
	}
	header.SetMode(mode)
	header.Modified = time.Unix(0, 0).UTC()
	writer, err := z.CreateHeader(header)
	if err != nil {
		return err
	}
	source, err := os.Open(path)
	if err != nil {
		return err
	}
	defer source.Close()
	_, err = io.Copy(writer, source)
	return err
}

func bootstrapHandler(token, runtimePath, workerTokenPath, wanTokenPath string) http.Handler {
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
		for _, path := range []string{runtimePath, workerTokenPath, wanTokenPath} {
			if info, err := os.Stat(path); err != nil || !info.Mode().IsRegular() {
				http.Error(w, "bootstrap payload is not ready", http.StatusServiceUnavailable)
				return
			}
		}

		w.Header().Set("Content-Type", "application/zip")
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("Content-Disposition", "attachment; filename=bees-bootstrap.zip")
		z := zip.NewWriter(w)
		if err := zipFile(z, "bees-remote-runtime.zip", runtimePath, 0o644); err != nil {
			log.Printf("[Bees tailnet] bootstrap runtime write failed: %v", err)
		}
		if err := zipFile(z, "training-worker.token", workerTokenPath, 0o600); err != nil {
			log.Printf("[Bees tailnet] bootstrap worker token write failed: %v", err)
		}
		if err := zipFile(z, "wan.token", wanTokenPath, 0o600); err != nil {
			log.Printf("[Bees tailnet] bootstrap WAN token write failed: %v", err)
		}
		if err := z.Close(); err != nil {
			log.Printf("[Bees tailnet] bootstrap zip close failed: %v", err)
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

func runGateway(args []string) error {
	fs := flag.NewFlagSet("gateway", flag.ContinueOnError)
	c := addCommon(fs)
	controlPort := fs.Int("control-port", 7150, "tailnet port proxying learner control")
	brokerPort := fs.Int("broker-port", 55051, "tailnet port proxying WAN broker")
	bootstrapPort := fs.Int("bootstrap-port", 7151, "tailnet bootstrap port")
	runtimePath := fs.String("runtime", "", "remote runtime zip path")
	workerTokenPath := fs.String("worker-token", "", "worker token path")
	wanTokenPath := fs.String("wan-token", "", "WAN token path")
	bootstrapTokenPath := fs.String("bootstrap-token", "", "bootstrap bearer token file")
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

	controlLn, err := s.Listen("tcp", fmt.Sprintf(":%d", *controlPort))
	if err != nil {
		return fmt.Errorf("listen control: %w", err)
	}
	defer controlLn.Close()
	brokerLn, err := s.Listen("tcp", fmt.Sprintf(":%d", *brokerPort))
	if err != nil {
		return fmt.Errorf("listen broker: %w", err)
	}
	defer brokerLn.Close()
	bootstrapLn, err := s.Listen("tcp", fmt.Sprintf(":%d", *bootstrapPort))
	if err != nil {
		return fmt.Errorf("listen bootstrap: %w", err)
	}
	defer bootstrapLn.Close()

	go proxyListener(ctx, controlLn, localDial(fmt.Sprintf("127.0.0.1:%d", *controlPort)), "control")
	go proxyListener(ctx, brokerLn, localDial(fmt.Sprintf("127.0.0.1:%d", *brokerPort)), "broker")
	httpServer := &http.Server{Handler: bootstrapHandler(token, *runtimePath, *workerTokenPath, *wanTokenPath)}
	go func() {
		if err := httpServer.Serve(bootstrapLn); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Printf("[Bees tailnet] bootstrap HTTP server failed: %v", err)
			cancel()
		}
	}()
	go func() {
		<-ctx.Done()
		shutdownCtx, shutdownCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer shutdownCancel()
		_ = httpServer.Shutdown(shutdownCtx)
		_ = controlLn.Close()
		_ = brokerLn.Close()
		_ = bootstrapLn.Close()
	}()

	log.Printf(
		"[Bees tailnet] gateway online ip=%s control=%d broker=%d bootstrap=%d",
		ip4, *controlPort, *brokerPort, *bootstrapPort,
	)
	<-ctx.Done()
	return nil
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
	if _, err := up(ctx, s); err != nil {
		return err
	}

	transport := &http.Transport{
		DialContext: func(ctx context.Context, network, address string) (net.Conn, error) {
			return s.Dial(ctx, network, address)
		},
	}
	defer transport.CloseIdleConnections()
	client := &http.Client{Transport: transport, Timeout: 5 * time.Minute}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, "http://"+*target+"/bootstrap", nil)
	if err != nil {
		return err
	}
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err := client.Do(req)
	if err != nil {
		return err
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
		go proxyListener(ctx, ln, tailnetDial(s, remote), local+" -> "+remote)
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
	fmt.Fprintln(os.Stderr, "Usage: bees-tailnet-bridge <auth|gateway|fetch|forward-multi> [options]")
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
