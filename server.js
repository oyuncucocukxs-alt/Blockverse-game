/**
 * BlockVerse Game Backend
 * Node.js 20 LTS + Express + Socket.IO
 * 
 * Handles: Auth, World persistence, Player data,
 *          Chat relay, Matchmaking, Admin APIs
 */

'use strict';

const express        = require('express');
const http           = require('http');
const { Server }     = require('socket.io');
const mongoose       = require('mongoose');
const Redis          = require('ioredis');
const cors           = require('cors');
const helmet         = require('helmet');
const compression    = require('compression');
const rateLimit      = require('express-rate-limit');
const winston        = require('winston');
const { createAdapter } = require('@socket.io/redis-adapter');

// ─────────────────────────────────────────────
// Configuration
// ─────────────────────────────────────────────

const config = {
  port:          process.env.PORT         || 3000,
  mongoUri:      process.env.MONGO_URI    || 'mongodb://localhost:27017/blockverse',
  redisHost:     process.env.REDIS_HOST   || 'localhost',
  redisPort:     parseInt(process.env.REDIS_PORT) || 6379,
  redisPassword: process.env.REDIS_PASS   || '',
  jwtSecret:     process.env.JWT_SECRET   || 'changeme-in-production',
  firebaseAdmin: process.env.FIREBASE_ADMIN_SDK_JSON,
  nodeEnv:       process.env.NODE_ENV     || 'development',
  logLevel:      process.env.LOG_LEVEL    || 'info',
  maxWorldSize:  parseInt(process.env.MAX_WORLD_SIZE) || 30000, // tiles
};

// ─────────────────────────────────────────────
// Logger
// ─────────────────────────────────────────────

const logger = winston.createLogger({
  level: config.logLevel,
  format: winston.format.combine(
    winston.format.timestamp(),
    winston.format.errors({ stack: true }),
    winston.format.json()
  ),
  transports: [
    new winston.transports.Console({
      format: winston.format.combine(
        winston.format.colorize(),
        winston.format.simple()
      )
    }),
    new winston.transports.File({ filename: 'logs/error.log',    level: 'error' }),
    new winston.transports.File({ filename: 'logs/combined.log' }),
  ]
});

// ─────────────────────────────────────────────
// App Setup
// ─────────────────────────────────────────────

const app    = express();
const server = http.createServer(app);

// Middleware
app.use(helmet({ contentSecurityPolicy: false }));
app.use(compression());
app.use(cors({ origin: '*', methods: ['GET', 'POST', 'PUT', 'DELETE', 'PATCH'] }));
app.use(express.json({ limit: '5mb' }));
app.use(express.urlencoded({ extended: true }));

// Global rate limiter (100 req/min per IP)
app.use(rateLimit({
  windowMs: 60 * 1000,
  max: 100,
  standardHeaders: true,
  legacyHeaders: false,
  handler: (req, res) => res.status(429).json({ error: 'Too many requests' })
}));

// Request logging
app.use((req, _res, next) => {
  logger.debug(`${req.method} ${req.path}`);
  next();
});

// ─────────────────────────────────────────────
// Database Connections
// ─────────────────────────────────────────────

async function connectMongo() {
  await mongoose.connect(config.mongoUri, {
    maxPoolSize: 20,
    socketTimeoutMS: 30000,
    connectTimeoutMS: 10000,
  });
  logger.info('✅ MongoDB connected');
}

// Redis (for caching and Socket.IO adapter)
const pubClient = new Redis({
  host: config.redisHost,
  port: config.redisPort,
  password: config.redisPassword || undefined,
  retryStrategy: (times) => Math.min(times * 50, 2000),
  enableReadyCheck: true,
});
const subClient = pubClient.duplicate();

pubClient.on('error',   err  => logger.error('Redis pub error', err));
pubClient.on('connect', ()   => logger.info('✅ Redis connected'));

// Global Redis export for other modules
global.redis = pubClient;

// ─────────────────────────────────────────────
// Socket.IO (Global Chat / Presence)
// ─────────────────────────────────────────────

const io = new Server(server, {
  cors: { origin: '*' },
  pingTimeout: 30000,
  pingInterval: 10000,
  transports: ['websocket'],
});

io.adapter(createAdapter(pubClient, subClient));

// Attach to global for controllers
global.io = io;

// Socket.IO namespaces
require('./src/sockets/chatSocket')(io);
require('./src/sockets/presenceSocket')(io);

// ─────────────────────────────────────────────
// Routes
// ─────────────────────────────────────────────

app.use('/v1/auth',      require('./src/routes/auth'));
app.use('/v1/players',   require('./src/routes/players'));
app.use('/v1/worlds',    require('./src/routes/worlds'));
app.use('/v1/inventory', require('./src/routes/inventory'));
app.use('/v1/economy',   require('./src/routes/economy'));
app.use('/v1/admin',     require('./src/routes/admin'));
app.use('/v1/matchmaking', require('./src/routes/matchmaking'));
app.use('/v1/leaderboard', require('./src/routes/leaderboard'));

// Health check
app.get('/health', (_req, res) => res.json({
  status: 'ok',
  uptime: process.uptime(),
  mongo: mongoose.connection.readyState === 1 ? 'ok' : 'down',
  timestamp: new Date().toISOString()
}));

// 404
app.use((_req, res) => res.status(404).json({ error: 'Not found' }));

// Error handler
app.use((err, _req, res, _next) => {
  logger.error('Unhandled error', { error: err.message, stack: err.stack });
  res.status(err.status || 500).json({
    error: config.nodeEnv === 'production' ? 'Internal server error' : err.message
  });
});

// ─────────────────────────────────────────────
// Bootstrap
// ─────────────────────────────────────────────

async function bootstrap() {
  try {
    await connectMongo();

    // Initialize Firebase Admin
    const admin = require('firebase-admin');
    if (!admin.apps.length) {
      const serviceAccount = config.firebaseAdmin
        ? JSON.parse(config.firebaseAdmin)
        : require('./config/firebase-service-account.json');

      admin.initializeApp({
        credential: admin.credential.cert(serviceAccount),
      });
      logger.info('✅ Firebase Admin initialized');
    }

    server.listen(config.port, () => {
      logger.info(`🚀 BlockVerse Backend listening on port ${config.port}`);
      logger.info(`🌍 Environment: ${config.nodeEnv}`);
    });
  } catch (err) {
    logger.error('Fatal startup error', err);
    process.exit(1);
  }
}

// Graceful shutdown
async function shutdown(signal) {
  logger.info(`${signal} received, shutting down gracefully`);
  server.close(async () => {
    await mongoose.disconnect();
    pubClient.disconnect();
    process.exit(0);
  });
}

process.on('SIGTERM', () => shutdown('SIGTERM'));
process.on('SIGINT',  () => shutdown('SIGINT'));
process.on('unhandledRejection', (reason) => {
  logger.error('Unhandled rejection', { reason });
});

bootstrap();

module.exports = { app, io };
