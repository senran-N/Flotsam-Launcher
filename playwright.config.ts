import { defineConfig } from '@playwright/test';
export default defineConfig({testDir:'./tests',fullyParallel:true,workers:2,webServer:{command:'npm run preview -- --port 1420 --strictPort',url:'http://127.0.0.1:1420',reuseExistingServer:true,timeout:30000},use:{baseURL:'http://127.0.0.1:1420',headless:true,channel:'chrome',viewport:{width:1280,height:820}},reporter:'list'});
