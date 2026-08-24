mergeInto(LibraryManager.library, {
    BeesInstallResponsiveViewport: function () {
        if (window.__beesResponsiveViewportInstalled) {
            return;
        }
        window.__beesResponsiveViewportInstalled = true;

        function setImportant(style, property, value) {
            if (style) {
                style.setProperty(property, value, 'important');
            }
        }

        function fillViewport() {
            var canvas = (typeof Module !== 'undefined' && Module.canvas)
                ? Module.canvas
                : document.getElementById('unity-canvas');
            var container = document.getElementById('unity-container') ||
                (canvas ? canvas.parentElement : null);
            var root = document.documentElement;
            var body = document.body;

            if (root) {
                setImportant(root.style, 'width', '100%');
                setImportant(root.style, 'height', '100%');
                setImportant(root.style, 'margin', '0');
                setImportant(root.style, 'padding', '0');
                setImportant(root.style, 'overflow', 'hidden');
            }
            if (body) {
                setImportant(body.style, 'width', '100%');
                setImportant(body.style, 'height', '100%');
                setImportant(body.style, 'margin', '0');
                setImportant(body.style, 'padding', '0');
                setImportant(body.style, 'overflow', 'hidden');
            }
            if (container) {
                setImportant(container.style, 'position', 'fixed');
                setImportant(container.style, 'left', '0');
                setImportant(container.style, 'top', '0');
                setImportant(container.style, 'right', '0');
                setImportant(container.style, 'bottom', '0');
                setImportant(container.style, 'width', '100vw');
                setImportant(container.style, 'height', '100vh');
                setImportant(container.style, 'min-width', '0');
                setImportant(container.style, 'min-height', '0');
                setImportant(container.style, 'max-width', 'none');
                setImportant(container.style, 'max-height', 'none');
                setImportant(container.style, 'aspect-ratio', 'auto');
                setImportant(container.style, 'transform', 'none');
            }
            if (canvas) {
                setImportant(canvas.style, 'position', 'absolute');
                setImportant(canvas.style, 'left', '0');
                setImportant(canvas.style, 'top', '0');
                setImportant(canvas.style, 'right', '0');
                setImportant(canvas.style, 'bottom', '0');
                setImportant(canvas.style, 'width', '100%');
                setImportant(canvas.style, 'height', '100%');
                setImportant(canvas.style, 'min-width', '0');
                setImportant(canvas.style, 'min-height', '0');
                setImportant(canvas.style, 'max-width', 'none');
                setImportant(canvas.style, 'max-height', 'none');
                setImportant(canvas.style, 'aspect-ratio', 'auto');
                setImportant(canvas.style, 'object-fit', 'fill');

                // Old deployed host pages may explicitly set matchWebGLToCanvasSize=false.
                // Keep those pages usable by resizing the drawing buffer from the runtime bundle.
                var cssWidth = Math.max(1, Math.round(canvas.clientWidth));
                var cssHeight = Math.max(1, Math.round(canvas.clientHeight));
                var dpr = window.devicePixelRatio || 1;
                var renderWidth = Math.max(1, Math.round(cssWidth * dpr));
                var renderHeight = Math.max(1, Math.round(cssHeight * dpr));
                if (typeof Module !== 'undefined' && typeof Module.setCanvasSize === 'function') {
                    Module.setCanvasSize(renderWidth, renderHeight);
                } else if (canvas.width !== renderWidth || canvas.height !== renderHeight) {
                    canvas.width = renderWidth;
                    canvas.height = renderHeight;
                }
            }

            // If the dedicated Bees page embeds Unity in a same-origin iframe, the iframe itself
            // is the outer viewport owner. Expand it too; cross-origin hosts remain untouched.
            try {
                var frame = window.frameElement;
                if (frame) {
                    setImportant(frame.style, 'position', 'fixed');
                    setImportant(frame.style, 'left', '0');
                    setImportant(frame.style, 'top', '0');
                    setImportant(frame.style, 'right', '0');
                    setImportant(frame.style, 'bottom', '0');
                    setImportant(frame.style, 'width', '100vw');
                    setImportant(frame.style, 'height', '100vh');
                    setImportant(frame.style, 'min-width', '0');
                    setImportant(frame.style, 'min-height', '0');
                    setImportant(frame.style, 'max-width', 'none');
                    setImportant(frame.style, 'max-height', 'none');
                    setImportant(frame.style, 'aspect-ratio', 'auto');
                    setImportant(frame.style, 'transform', 'none');

                    var hostDocument = frame.ownerDocument;
                    if (hostDocument && hostDocument.documentElement) {
                        setImportant(hostDocument.documentElement.style, 'width', '100%');
                        setImportant(hostDocument.documentElement.style, 'height', '100%');
                        setImportant(hostDocument.documentElement.style, 'margin', '0');
                        setImportant(hostDocument.documentElement.style, 'padding', '0');
                        setImportant(hostDocument.documentElement.style, 'overflow', 'hidden');
                    }
                    if (hostDocument && hostDocument.body) {
                        setImportant(hostDocument.body.style, 'width', '100%');
                        setImportant(hostDocument.body.style, 'height', '100%');
                        setImportant(hostDocument.body.style, 'margin', '0');
                        setImportant(hostDocument.body.style, 'padding', '0');
                        setImportant(hostDocument.body.style, 'overflow', 'hidden');
                    }
                }
            } catch (_) {
                // Cross-origin embedding pages must size their own iframe.
            }
        }

        window.__beesFillResponsiveViewport = fillViewport;
        window.addEventListener('resize', fillViewport);
        fillViewport();
        window.requestAnimationFrame(fillViewport);
    }
});
