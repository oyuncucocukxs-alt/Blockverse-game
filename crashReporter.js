'use strict';

const fs      = require('fs');
const path    = require('path');
const winston = require('winston');

// ─── Logger setup ──────────────────────────────────────────────────────────────

const crashLogger = winston.createLogger({
  level: 'error',
  format: winston.format.combine(
    winston.format.timestamp(),
    winston.format.errors({ stack: true }),
    winston.format.json()
  ),
  transports: [
    new winston.transports.File({
      filename: path.join(__dirname, '../../logs/crash.log'),
      maxsize:  10 * 1024 * 1024, // 10MB
      maxFiles: 5,
      tailable: true,
    }),
  ],
});

// ─── Express Error Handler Middleware ─────────────────────────────────────────

function errorHandler(err, req, res, _next) {
  const status = err.status || err.statusCode || 500;
  const isOperational = status < 500;

  // Log to crash file
  crashLogger.error({
    message:    err.message,
    stack:      err.stack,
    path:       req.path,
    method:     req.method,
    ip:         req.ip,
    userId:     req.playerId ?? null,
    statusCode: status,
    timestamp:  new Date().toISOString(),
  });

  // Don't leak internal details in production
  const body = process.env.NODE_ENV === 'production' && !isOperational
    ? { error: 'Internal server error', code: 'INTERNAL_ERROR' }
    : { error: err.message, stack: err.stack };

  return res.status(status).json(body);
}

// ─── Unhandled Rejection / Exception handlers ─────────────────────────────────

function registerProcessHandlers() {
  process.on('uncaughtException', err => {
    crashLogger.error({ type: 'uncaughtException', message: err.message, stack: err.stack });
    console.error('[FATAL] uncaughtException:', err);
    // Give logger time to flush, then exit
    setTimeout(() => process.exit(1), 500);
  });

  process.on('unhandledRejection', (reason, promise) => {
    crashLogger.error({
      type:    'unhandledRejection',
      reason:  String(reason),
      promise: String(promise),
    });
    console.error('[ERROR] unhandledRejection:', reason);
  });
}

// ─── Request Logger Middleware ─────────────────────────────────────────────────

const accessLogger = winston.createLogger({
  level: 'http',
  format: winston.format.combine(
    winston.format.timestamp(),
    winston.format.json()
  ),
  transports: [
    new winston.transports.File({
      filename: path.join(__dirname, '../../logs/access.log'),
      maxsize:  20 * 1024 * 1024,
      maxFiles: 7,
    }),
  ],
});

function requestLogger(req, res, next) {
  const start = Date.now();
  res.on('finish', () => {
    accessLogger.http({
      method:     req.method,
      path:       req.path,
      status:     res.statusCode,
      ms:         Date.now() - start,
      ip:         req.ip,
      userId:     req.playerId ?? null,
      userAgent:  req.get('user-agent'),
    });
  });
  next();
}

// ─── Rate Limit Violation Logger ──────────────────────────────────────────────

function rateLimitLogger(req, res) {
  crashLogger.warn({
    type:   'rate_limit',
    path:   req.path,
    ip:     req.ip,
    userId: req.playerId ?? null,
  });
  res.status(429).json({ error: 'Too many requests. Please slow down.' });
}

// ─── Mongo Health Monitor ─────────────────────────────────────────────────────

function startMongoMonitor(mongoose) {
  mongoose.connection.on('disconnected', () => {
    crashLogger.error({ type: 'mongo_disconnect', timestamp: new Date().toISOString() });
  });
  mongoose.connection.on('reconnected', () => {
    crashLogger.info({ type: 'mongo_reconnect', timestamp: new Date().toISOString() });
  });
  mongoose.connection.on('error', err => {
    crashLogger.error({ type: 'mongo_error', message: err.message, stack: err.stack });
  });
}

module.exports = {
  errorHandler,
  requestLogger,
  rateLimitLogger,
  registerProcessHandlers,
  startMongoMonitor,
  crashLogger,
};
