import axios from "axios";
import { config } from "./config";

const oneData = axios.create({ timeout: 50000 });
oneData.defaults.baseURL = config.oneData.url;
oneData.defaults.headers.common['Authorization'] = config.oneData.token


