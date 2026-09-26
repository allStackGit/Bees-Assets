package main

import (
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
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
