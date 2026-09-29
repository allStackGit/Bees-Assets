'use strict';

const {
    RL_TELEMETRY_REQUEST_TYPES,
    RlTelemetryUploadManager,
} = require('./rlTelemetryUploads');

const INSTALL_MARKER = Symbol('beesRlTelemetryUploadSecurity');

function sendResponse(request, status, extra = {}) {
    request.respond({ Type: request.params.Type, Hash: request.params.Hash, Status: status, ...extra });
}

function getManager(server, options) {
    if (options.rlTelemetryUploadManager) return options.rlTelemetryUploadManager;
    if (Object.prototype.hasOwnProperty.call(server, '__beesRlTelemetryUploadManager')) {
        return server.__beesRlTelemetryUploadManager;
    }
    const root = options.rlTelemetryUploadRoot || process.env.BEES_RL_TELEMETRY_UPLOAD_DIR;
    const manager = root
        ? new RlTelemetryUploadManager(root, options.rlTelemetryUploadOptions)
        : null;
    Object.defineProperty(server, '__beesRlTelemetryUploadManager', {
        configurable: false,
        enumerable: false,
        writable: false,
        value: manager,
    });
    return manager;
}

function testUserId(server, params) {
    if (!server?.test) return null;
    const value = params?.UserId;
    if (typeof value !== 'string' || !value.trim() || value.length > 128) return null;
    return value.trim();
}

function installRlTelemetryUploadSecurity(runtime, options = {}) {
    if (!runtime?.SocketConnection || runtime[INSTALL_MARKER]) return runtime;
    runtime[INSTALL_MARKER] = true;
    const LegacySocketConnection = runtime.SocketConnection;

    class RlTelemetryUploadSocketConnection extends LegacySocketConnection {
        constructor(connection, db, server, id) {
            super(connection, db, server, id);
            const legacyHandleMessage = this.handleMessage.bind(this);
            this.handleMessage = async request => {
                if (!RL_TELEMETRY_REQUEST_TYPES.has(request?.params?.Type)) {
                    return legacyHandleMessage(request);
                }
                // Production remains strictly Steam-authenticated. The legacy test server deliberately
                // has no Steam authentication layer, so only server.test may use the request's test ID.
                const userId = this.authenticatedUserId
                    ? String(this.authenticatedUserId)
                    : testUserId(server, request.params);
                if (!userId) {
                    sendResponse(request, 401, { ErrorCode: 'authentication-required' });
                    return false;
                }
                const manager = getManager(server, options);
                if (!manager) {
                    sendResponse(request, 503, { ErrorCode: 'telemetry-upload-disabled' });
                    return true;
                }
                try {
                    const result = await manager.handle(request.params, {
                        userId,
                        connectionId: String(id),
                    });
                    sendResponse(request, 200, result);
                } catch (error) {
                    const status = Number.isInteger(error?.statusCode) ? error.statusCode : 500;
                    const errorCode = typeof error?.code === 'string' && error.code
                        ? error.code
                        : 'telemetry-upload-failed';
                    if (status >= 500) runtime.common.handleError(error, 'RL telemetry upload');
                    sendResponse(request, status, { ErrorCode: errorCode });
                }
                return true;
            };
        }
    }

    runtime.SocketConnection = RlTelemetryUploadSocketConnection;
    return runtime;
}

module.exports = {
    installRlTelemetryUploadSecurity,
    testUserId,
};
