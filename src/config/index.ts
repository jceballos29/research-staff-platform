import dotenv from 'dotenv';

dotenv.config();

export const config = {
  nodeEnv: process.env.NODE_ENV || 'development',
  port: process.env.PORT || 4000,
  cors: {
    origin: process.env.CORS_ORIGIN || '*',
    credentials: true,
  },
  oneData: {
    url: process.env.ONE_DATA_URL || 'https://api.onedata.com',
    token: process.env.ONE_DATA_TOKEN || '',
  }
} as const;