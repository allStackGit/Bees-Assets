package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"syscall"
	"time"

	"tailscale.com/tsnet"
)

type commonFlags struct {
	stateDir string
	hostname string
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
	s := &tsnet.Server{
		Dir:      filepath.Clean(c.stateDir),
		Hostname: c.hostname,
		UserLogf: func(format string, args ...any) {
			fmt.Printf("[Bees tailnet] "+format+"\n", args...)
		},
	}
	return s, nil
}

func runAuth(args []string) error {
	fs := flag.NewFlagSet("auth", flag.ContinueOnError)
	c := addCommon(fs)
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

	status, err := s.Up(ctx)
	if err != nil {
		return err
	}
	fmt.Printf("[Bees tailnet] authenticated hostname=%s tailscale_ip=%s\n", c.hostname, status.TailscaleIPs[0])
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

func runServe(args []string) error {
	fs := flag.NewFlagSet("serve", flag.ContinueOnError)
	c := addCommon(fs)
	listen := fs.String("listen", ":2222", "tailnet TCP listen address")
	target := fs.String("target", "127.0.0.1:22", "local TCP target")
	if err := fs.Parse(args); err != nil {
		return err
	}
	s, err := server(c)
	if err != nil {
		return err
	}
	defer s.Close()

	ln, err := s.Listen("tcp", *listen)
	if err != nil {
		return err
	}
	defer ln.Close()
	log.Printf("[Bees tailnet] serving %s -> %s as %s", *listen, *target, c.hostname)

	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	go func() {
		<-ctx.Done()
		_ = ln.Close()
	}()

	for {
		src, err := ln.Accept()
		if err != nil {
			if ctx.Err() != nil {
				return nil
			}
			return err
		}
		go func() {
			dst, err := net.DialTimeout("tcp", *target, 15*time.Second)
			if err != nil {
				log.Printf("[Bees tailnet] local target %s unavailable: %v", *target, err)
				_ = src.Close()
				return
			}
			copyConn(src, dst)
		}()
	}
}

func runForward(args []string) error {
	fs := flag.NewFlagSet("forward", flag.ContinueOnError)
	c := addCommon(fs)
	listen := fs.String("listen", "127.0.0.1:2222", "local TCP listen address")
	target := fs.String("target", "", "tailnet TCP target, for example bees-learner:2222")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if strings.TrimSpace(*target) == "" {
		return errors.New("--target is required")
	}
	s, err := server(c)
	if err != nil {
		return err
	}
	defer s.Close()

	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	if _, err := s.Up(ctx); err != nil {
		return err
	}

	ln, err := net.Listen("tcp", *listen)
	if err != nil {
		return err
	}
	defer ln.Close()
	log.Printf("[Bees tailnet] forwarding %s -> %s as %s", *listen, *target, c.hostname)

	go func() {
		<-ctx.Done()
		_ = ln.Close()
	}()

	for {
		src, err := ln.Accept()
		if err != nil {
			if ctx.Err() != nil {
				return nil
			}
			return err
		}
		go func() {
			dialCtx, dialCancel := context.WithTimeout(ctx, 30*time.Second)
			defer dialCancel()
			dst, err := s.Dial(dialCtx, "tcp", *target)
			if err != nil {
				log.Printf("[Bees tailnet] tailnet target %s unavailable: %v", *target, err)
				_ = src.Close()
				return
			}
			copyConn(src, dst)
		}()
	}
}

func usage() {
	fmt.Fprintln(os.Stderr, "Usage: bees-tailnet-bridge <auth|serve|forward> [options]")
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
	case "serve":
		err = runServe(os.Args[2:])
	case "forward":
		err = runForward(os.Args[2:])
	default:
		usage()
		os.Exit(2)
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, "error:", err)
		os.Exit(1)
	}
}
