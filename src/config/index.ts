import dotenv from 'dotenv';

dotenv.config();

export const config = {
  nodeEnv: process.env.NODE_ENV || 'development',
  port: process.env.PORT || 4000,
  cors: {
    origin: process.env.CORS_ORIGIN || '*',
    credentials: true,
  }
} as const;