import { test, expect } from '@playwright/test';
import { fixture } from './scene-fixture';
import type { Config, ConfigResponse, AppState } from '../src/lib/types';
const live = !!process.env.POLTERSENSE_E2E_URL;
let restoreConfig: Config | undefined;
test.afterEach(async ({ request }) => {
	if (live && restoreConfig) {
		const latest: ConfigResponse = await (await request.get('/api/config')).json();
		const restored = await request.put('/api/config', { data: { revision: latest.revision, config: restoreConfig } });
		expect(restored.ok()).toBe(true);
	}
});
test('map, placement persistence, invalid YAML and synthetic diagnostics', async ({ page, request }) => {
	let scene: ConfigResponse = live ? await (await request.get('/api/config')).json() : fixture();
	const original = structuredClone(scene.config);
	restoreConfig = original;
	const secondPage = live ? await page.context().newPage() : null;
	if (secondPage) await secondPage.goto('/');
	const runtime: AppState = { schemaVersion: 1, configRevision: scene.revision, mqtt: { status: 'unconfigured', testInputEnabled: false }, diagnostics: { accepted: 0, rejected: 0, dropped: 0, stale: 0, duplicate: 0, outOfOrder: 0, retained: 0, latest: [] }, medium: { schemaVersion: 1, availability: 'notImplemented', inputHealth: 'noInput', rooms: scene.config.floors.flatMap((f) => f.rooms.map((r) => ({ roomId: r.id, state: 'unknown' }))), tracks: null }, demoEnabled: true };
	const errors: string[] = [];
	page.on('pageerror', (error) => errors.push(error.message));
	if (!live) {
		await page.routeWebSocket('**/ws', (socket) => {
			socket.send(JSON.stringify(runtime));
		});
		await page.route('**/api/**', async (route) => {
			const path = new URL(route.request().url()).pathname;
			if (path === '/api/config/yaml') {
				await route.fulfill({ status: 400, json: { error: 'Invalid YAML' } });
				return;
			}
			if (path === '/api/config') {
				if (route.request().method() === 'PUT') {
					const body = route.request().postDataJSON();
					expect(body.revision).toBe(scene.revision);
					scene = { ...scene, revision: 'fixture-scene-2', config: body.config };
					runtime.configRevision = scene.revision;
				}
				await route.fulfill({ json: scene });
				return;
			}
			if (path === '/api/demo/replay') {
				runtime.diagnostics.accepted += 1;
				runtime.diagnostics.latest = [{ wispId: scene.config.wisps[0].id, linkId: scene.config.links[0].id, sourceId: scene.config.sources[0].id, receivedAt: '2026-10-03T00:00:00Z', motion: true, score: 0.8, provenance: { origin: 'replay', adapter: 'synthetic', referenceOrigin: 'configured', radioOrigin: 'observed' }, configRevision: 'recorded-fictional-scene', freshness: 'replay' }];
				await route.fulfill({ json: {} });
				return;
			}
			await route.fulfill({ json: runtime });
		});
	}
	try {
		await page.goto('/');
		await expect(page.getByRole('heading', { name: 'PolterSense', exact: true })).toBeVisible();
		await expect(page.getByRole('heading', { name: scene.config.name, exact: true })).toBeVisible();
		const firstFloor = scene.config.floors[0];
		const otherFloor = scene.config.floors[1];
		await expect(page.locator('.map-canvas').getByText(firstFloor.rooms[0].name, { exact: true })).toBeVisible();
		await page.getByLabel('Floor', { exact: true }).selectOption(otherFloor.id);
		await expect(page.locator('.map-canvas').getByText(otherFloor.rooms[0].name, { exact: true })).toBeVisible();
		await page.getByLabel('Floor', { exact: true }).selectOption(firstFloor.id);
		await expect(page.getByRole('button', { name: `Wisp ${scene.config.wisps[0].name}`, exact: true })).toBeVisible();
		await page.locator('.map-canvas').focus();
		await page.keyboard.press('+');
		await page.getByRole('button', { name: 'Reset view', exact: true }).click();
		await page.getByRole('button', { name: `Wisp ${scene.config.wisps[0].name}`, exact: true }).click();
		await expect(page.getByLabel('Stable ID', { exact: true })).toHaveAttribute('readonly', '');
		const modifiedName = 'Browser verified Wisp';
		await page.getByLabel('Display name', { exact: true }).fill(modifiedName);
		await page.getByLabel('X coordinate', { exact: true }).fill('1.75');
		await page.getByRole('button', { name: 'Save configuration', exact: true }).click();
		await expect(page.getByRole('button', { name: `Wisp ${modifiedName}`, exact: true })).toBeVisible();
		if (live) {
			const persisted: ConfigResponse = await (await request.get('/api/config')).json();
			expect(persisted.config.wisps[0].name).toBe(modifiedName);
			expect(persisted.config.wisps[0].point[0]).toBe(1.75);
		}
		if (secondPage) await expect(secondPage.getByRole('button', { name: `Wisp ${modifiedName}`, exact: true })).toBeVisible();
		// Source metadata/coordinates and link assignment use the same authoritative service.
		await page.getByRole('button', { name: `Source ${original.sources[0].name}`, exact: true }).click();
		await page.getByLabel('Display name', { exact: true }).fill('Browser verified Source');
		await page.getByLabel('Physical AP label (optional)', { exact: true }).fill('Fictional test AP');
		await page.getByLabel('Z coordinate', { exact: true }).fill('2.25');
		await page.getByRole('button', { name: 'Save configuration', exact: true }).click();
		await expect(page.getByRole('button', { name: 'Source Browser verified Source', exact: true })).toBeVisible();
		await page.locator('.inventory-item').filter({ hasText: original.links[0].id }).click();
		await page.getByRole('combobox', { name: 'Source', exact: true }).selectOption(original.sources[1].id);
		await page.getByRole('button', { name: 'Save configuration', exact: true }).click();
		await expect(page.locator('.inventory-item').filter({ hasText: original.links[0].id })).toContainText(original.sources[1].id);
		if (live) {
			const persisted: ConfigResponse = await (await request.get('/api/config')).json();
			expect(persisted.config.sources[0].point[2]).toBe(2.25);
			expect(persisted.config.sources[0].apLabel).toBe('Fictional test AP');
			expect(persisted.config.links[0].sourceId).toBe(original.sources[1].id);
		}
		await page.getByRole('button', { name: 'YAML', exact: true }).click();
		await page.getByLabel('Configuration YAML', { exact: true }).fill('schema_version: [invalid');
		await page.getByRole('button', { name: 'Save YAML', exact: true }).click();
		await expect(page.getByRole('status')).toContainText('Request failed');
		await expect(page.getByLabel('Configuration YAML', { exact: true })).toHaveValue('schema_version: [invalid');
		await expect(page.getByRole('button', { name: `Wisp ${modifiedName}`, exact: true })).toBeVisible();
		await page.getByRole('button', { name: 'Diagnostics', exact: true }).click();
		await expect(page.getByText('Inference not implemented', { exact: true })).toBeVisible();
		await expect(page.getByRole('button', { name: 'Replay synthetic Echo', exact: true })).toBeVisible();
		await page.getByRole('button', { name: 'Replay synthetic Echo', exact: true }).click();
		if (!live) {
			await page.reload();
			await page.getByRole('button', { name: 'Diagnostics', exact: true }).click();
		}
		await expect(page.getByTestId('accepted-count')).not.toHaveText('0');
		await expect(page.locator('.observation').first()).toContainText(/synthetic|replay/i);
		await expect(page.getByText('Occupancy unknown · no estimated tracks', { exact: true })).toBeVisible();
		await expect(page.locator('.room-status')).toContainText('all estimates unknown');
		expect(errors).toEqual([]);
		await page.screenshot({ path: process.env.POLTERSENSE_SCREENSHOT ?? '/tmp/poltersense-ui-smoke.png', fullPage: true });
	} finally {
		await secondPage?.close();
	}
});
