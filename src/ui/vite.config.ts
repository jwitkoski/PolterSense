import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';
export default defineConfig({
	plugins: [sveltekit()],
	server: { port: 5173, host: '127.0.0.1', proxy: { '/api': 'http://localhost:5279', '/ws': { target: 'ws://localhost:5279', ws: true } } },
	preview: { port: 4173, host: '127.0.0.1' }
});
