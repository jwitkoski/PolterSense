import { test, expect, type Page } from '@playwright/test';
import { fixture } from './scene-fixture';
import type { EspectreSnapshot } from '../src/lib/types';
const snapshot: EspectreSnapshot = {
	address: '10.20.30.40',
	port: 62587,
	device: { deviceId: '0123456789abcdef', name: 'espectre', label: 'Firmware label', frontend: 'native', firmware: '3.0.0-rc3', chip: 'ESP32S3' },
	capabilities: { protocolVersion: '1.0', canUpdateDevice: true, canUpdateSensing: true },
	sensing: { enabled: true, ready: true, calibrating: false, mode: 'sensing', derivedEventsPaused: false, detector: 'lightweight', threshold: 0.7, motionOnHits: 2, motionOffHits: 3, trafficGeneratorMode: 'ping' },
	readAt: '2026-10-03T00:00:00Z'
};
async function prepare(page: Page, candidate = snapshot, mapped = true) {
	let scene = fixture();
	if (mapped) Object.assign(scene.config.wisps[0], { externalId: candidate.device.deviceId, directHttp: { address: candidate.address, port: candidate.port } });
	const writes: unknown[] = [];
	const summary = () => ({ schemaVersion: 1, configRevision: scene.revision, mqtt: { status: 'unconfigured', testInputEnabled: false }, diagnostics: { accepted: 0, rejected: 0, dropped: 0, stale: 0, duplicate: 0, outOfOrder: 0, retained: 0, latest: [] }, medium: { schemaVersion: 1, availability: 'notImplemented', inputHealth: 'noInput', rooms: [], tracks: null }, demoEnabled: false });
	await page.routeWebSocket('**/ws', (socket) => socket.send(JSON.stringify(summary())));
	// Always intercept, including live-backend runs: these write-path tests must never command hardware.
	await page.route('**/api/**', async (route) => {
		const path = new URL(route.request().url()).pathname;
		if (path === '/api/config') {
			if (route.request().method() === 'PUT') scene = { ...scene, config: route.request().postDataJSON().config, revision: 'updated-scene' };
			await route.fulfill({ json: scene });
		} else if (path === '/api/wisps/discover') await route.fulfill({ json: { candidates: [candidate], status: 'complete', message: 'Found one receiver.' } });
		else if (path.endsWith('/espectre')) {
			if (route.request().method() === 'PATCH') {
				writes.push(route.request().postDataJSON());
				await route.fulfill({ json: { confirmed: false, code: 'notConfirmed', message: 'Readback did not match.', snapshot: candidate } });
			} else await route.fulfill({ json: candidate });
		} else await route.fulfill({ json: summary() });
	});
	await page.goto('/');
	await page.getByRole('button', { name: 'Wisp setup', exact: true }).click();
	await page.getByRole('button', { name: /Firmware label 0123456789abcdef/ }).click();
	return { writes, scene: () => scene };
}
test('discovery never writes; label and sensing writes stay separate and unconfirmed drafts persist', async ({ page }) => {
	const { writes } = await prepare(page);
	expect(writes).toEqual([]);
	await page.getByLabel('Firmware label', { exact: true }).fill('New label');
	await page.getByRole('button', { name: 'Apply label to device' }).click();
	await expect(page.getByText(/Changes were not confirmed/)).toBeVisible();
	expect(writes).toEqual([{ revision: 'fixture-scene-1', expectedDeviceId: '0123456789abcdef', label: 'New label' }]);
	await expect(page.getByLabel('Firmware label', { exact: true })).toHaveValue('New label');
	await expect(page.getByText(/confirmed by readback/)).toHaveCount(0);
	await page.getByLabel('Threshold', { exact: true }).fill('0.8');
	await page.getByRole('button', { name: 'Apply sensing to device' }).click();
	await expect.poll(() => writes.length).toBe(2);
	expect(writes[1]).toEqual({ revision: 'fixture-scene-1', expectedDeviceId: '0123456789abcdef', sensing: { enabled: true, detector: 'lightweight', threshold: 0.8, motionOnHits: 2, motionOffHits: 3, trafficGeneratorMode: 'ping' } });
});
test('unsupported sensing and label operations are unavailable', async ({ page }) => {
	const { writes } = await prepare(page, { ...snapshot, sensing: null, capabilities: { ...snapshot.capabilities, canUpdateDevice: false, canUpdateSensing: false } });
	await expect(page.getByRole('button', { name: 'Apply label to device' })).toBeDisabled();
	await expect(page.getByRole('button', { name: 'Apply sensing to device' })).toBeDisabled();
	await expect(page.getByText('Unavailable', { exact: true })).toBeVisible();
	expect(writes).toEqual([]);
});
test('binding preserves local placement and ordinary inventory edits preserve the endpoint', async ({ page }) => {
	const context = await prepare(page, snapshot, false);
	await page.getByRole('combobox', { name: 'Local Wisp', exact: true }).selectOption('existing:wisp-one');
	await page.getByRole('button', { name: 'Save local mapping' }).click();
	await expect(page.getByText('Receiver mapped in local configuration. No device settings were changed.')).toBeVisible();
	expect(context.scene().config.wisps[0]).toMatchObject({ name: 'Study Wisp', point: [1, 1, 1], externalId: '0123456789abcdef', directHttp: { address: '10.20.30.40', port: 62587 } });
	await page.getByRole('button', { name: 'Inventory', exact: true }).click();
	await page.getByRole('button', { name: /^Study Wisp wisp-one/ }).click();
	await page.getByLabel('X coordinate', { exact: true }).fill('2');
	await page.getByRole('button', { name: 'Save configuration', exact: true }).click();
	await expect(page.getByRole('button', { name: /^Study Wisp wisp-one/ })).toContainText('2, 1, 1');
	expect(context.scene().config.wisps[0].directHttp).toEqual({ address: '10.20.30.40', port: 62587 });
	expect(context.writes).toEqual([]);
});

test('new Wisps require explicit coordinates instead of inferred placement', async ({ page }) => {
	const context = await prepare(page, snapshot, false);
	await page.getByRole('combobox', { name: 'Local Wisp', exact: true }).selectOption('new');
	await page.getByLabel('New Wisp stable ID', { exact: true }).fill('spare-wisp');
	await page.getByLabel('New Wisp display name', { exact: true }).fill('Spare receiver');
	for (const axis of ['X', 'Y', 'Z']) await expect(page.getByLabel(`New Wisp ${axis} coordinate`)).toHaveValue('');
	await page.getByRole('button', { name: 'Save local mapping' }).click();
	expect(context.scene().config.wisps).toHaveLength(1);
	await page.getByLabel('New Wisp X coordinate').fill('2');
	await page.getByLabel('New Wisp Y coordinate').fill('3');
	await page.getByLabel('New Wisp Z coordinate').fill('1');
	await page.getByRole('button', { name: 'Save local mapping' }).click();
	await expect(page.getByText('Receiver mapped in local configuration. No device settings were changed.')).toBeVisible();
	expect(context.scene().config.wisps[1]).toMatchObject({ id: 'spare-wisp', name: 'Spare receiver', point: [2, 3, 1], externalId: '0123456789abcdef' });
	expect(context.writes).toEqual([]);
});
