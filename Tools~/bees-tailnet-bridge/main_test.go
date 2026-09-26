package main

import (
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestBootstrapHandlerServesExactPublishedBundle(t *testing.T) {
	root := t.TempDir()
	bundlePath := filepath.Join(root, "bootstrap.zip")
	expected := []byte("complete-atomic-bootstrap-bundle")
	if err := os.WriteFile(bundlePath, expected, 0o600); err != nil {
		t.Fatal(err)
	}

	handler := bootstrapHandler("secret", bundlePath)
	request := httptest.NewRequest(http.MethodGet, "/bootstrap", nil)
	request.Header.Set("Authorization", "Bearer secret")
	response := httptest.NewRecorder()

	handler.ServeHTTP(response, request)

	if response.Code != http.StatusOK {
		t.Fatalf("expected 200, got %d: %s", response.Code, response.Body.String())
	}
	if got := response.Body.Bytes(); string(got) != string(expected) {
		t.Fatalf("served bytes differ: got %q want %q", got, expected)
	}
	if got := response.Header().Get("Content-Type"); got != "application/zip" {
		t.Fatalf("unexpected content type %q", got)
	}
	if got := response.Header().Get("Cache-Control"); got != "no-store" {
		t.Fatalf("unexpected cache control %q", got)
	}
}

func TestBootstrapHandlerRequiresBearerToken(t *testing.T) {
	root := t.TempDir()
	bundlePath := filepath.Join(root, "bootstrap.zip")
	if err := os.WriteFile(bundlePath, []byte("bundle"), 0o600); err != nil {
		t.Fatal(err)
	}

	handler := bootstrapHandler("secret", bundlePath)
	request := httptest.NewRequest(http.MethodGet, "/bootstrap", nil)
	response := httptest.NewRecorder()

	handler.ServeHTTP(response, request)

	if response.Code != http.StatusUnauthorized {
		t.Fatalf("expected 401, got %d", response.Code)
	}
}

func TestBootstrapHandlerFailsClosedWhenBundleIsMissing(t *testing.T) {
	handler := bootstrapHandler("secret", filepath.Join(t.TempDir(), "missing.zip"))
	request := httptest.NewRequest(http.MethodGet, "/bootstrap", nil)
	request.Header.Set("Authorization", "Bearer secret")
	response := httptest.NewRecorder()

	handler.ServeHTTP(response, request)

	if response.Code != http.StatusServiceUnavailable {
		t.Fatalf("expected 503, got %d", response.Code)
	}
}


func TestWriteGatewayHealthCreatesAndRefreshesHeartbeat(t *testing.T) {
	root := t.TempDir()
	healthPath := filepath.Join(root, "gateway-health.txt")
	if err := writeGatewayHealth(healthPath, "100.64.0.1", 7150, 55051, 7151); err != nil {
		t.Fatal(err)
	}
	first, err := os.Stat(healthPath)
	if err != nil {
		t.Fatal(err)
	}
	payload, err := os.ReadFile(healthPath)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(payload), "ready ip=100.64.0.1") {
		t.Fatalf("unexpected health payload %q", string(payload))
	}

	time.Sleep(10 * time.Millisecond)
	if err := writeGatewayHealth(healthPath, "100.64.0.1", 7150, 55051, 7151); err != nil {
		t.Fatal(err)
	}
	second, err := os.Stat(healthPath)
	if err != nil {
		t.Fatal(err)
	}
	if second.ModTime().Before(first.ModTime()) {
		t.Fatalf("gateway heartbeat moved backwards: first=%v second=%v", first.ModTime(), second.ModTime())
	}
}


func TestGatewaySupervisorUsesDistinctRecoverableChildOwnerToken(t *testing.T) {
	args := []string{
		"--state", "state",
		"--owner-token", "owner-secret",
		"--health-file=health.txt",
		"--control-port", "7150",
	}
	ownerToken := managedFlagValue(args, "--owner-token")
	childArgs := withoutManagedOwnerToken(args)
	childToken := managedChildOwnerToken(ownerToken)
	childArgs = append(childArgs, "--owner-token", childToken)

	if got := managedFlagValue(childArgs, "--owner-token"); got != childToken {
		t.Fatalf("unexpected child owner token %q", got)
	}
	if strings.Contains(childToken, ownerToken) {
		t.Fatalf("child owner token must not contain parent token: parent=%q child=%q", ownerToken, childToken)
	}
	if len(childToken) != 64 {
		t.Fatalf("unexpected child owner token length %d", len(childToken))
	}
	if got := managedFlagValue(childArgs, "--health-file"); got != "health.txt" {
		t.Fatalf("unexpected health file %q", got)
	}
}
