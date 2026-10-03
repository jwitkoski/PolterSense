import type { Page } from '@playwright/test';
import { fixture } from './scene-fixture';
/** Only mock the application-owned contracts; live smoke uses its isolated backend. */
export async function prepareMap(page: Page) {
	if (process.env.POLTERSENSE_E2E_URL) return;
	const scene = fixture();
	const summary = { schemaVersion: 1, configRevision: scene.revision, mqtt: { status: 'unconfigured', testInputEnabled: false }, diagnostics: { accepted: 0, rejected: 0, dropped: 0, stale: 0, duplicate: 0, outOfOrder: 0, retained: 0, latest: [] }, medium: { schemaVersion: 1, availability: 'notImplemented', inputHealth: 'noInput', rooms: [], tracks: null }, demoEnabled: false };
	await page.route('**/api/config', (route) => route.fulfill({ json: scene }));
	await page.route('**/api/state', (route) => route.fulfill({ json: summary }));
	// Real summaries arrive once per second; periodic updates guard against resetting cursor state.
	await page.routeWebSocket('**/ws', (socket) => {
		socket.send(JSON.stringify(summary));
		const timer = setInterval(() => socket.send(JSON.stringify(summary)), 1000);
		socket.onClose(() => clearInterval(timer));
	});
}
