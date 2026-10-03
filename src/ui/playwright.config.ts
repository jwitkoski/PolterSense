import { defineConfig } from '@playwright/test';
const baseURL = process.env.POLTERSENSE_E2E_URL;
export default defineConfig({
	testDir: './tests',
	timeout: 60000,
	testMatch: '**/*.spec.ts',
	workers: 1,
	expect: { timeout: 10000 },
	use: { baseURL: baseURL ?? 'http://127.0.0.1:4173', launchOptions: { executablePath: process.env.POLTERSENSE_CHROME, args: ['--no-sandbox'] } },
	webServer: baseURL ? undefined : { command: 'pnpm run preview --host 127.0.0.1 --port 4173', url: 'http://127.0.0.1:4173', timeout: 30000, reuseExistingServer: false }
});
