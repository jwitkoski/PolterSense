import { test, expect } from '@playwright/test';
import { prepareMap } from './map-fixture';

// Retained upstream regression: the readout is mouse-owned state, not derived state.
test('coordinate readout tracks the mouse and holds its value across live summaries', async ({ page }) => {
	let frames = 0;
	page.on('websocket', (socket) => socket.on('framereceived', () => frames++));
	await prepareMap(page);
	await page.goto('/');
	const readout = page.getByText(/^X: .*, Y: /);
	await expect(readout).toBeVisible();
	await expect(readout).toHaveText('X: 0, Y: 0');
	const box = await page.locator('.map-canvas svg').boundingBox();
	if (!box) throw new Error('map SVG has no box');
	await page.mouse.move(box.x + box.width * 0.6, box.y + box.height * 0.6);
	await expect(readout).not.toHaveText('X: 0, Y: 0');
	const moved = await readout.textContent();
	const frameAtMove = frames;
	if (process.env.POLTERSENSE_E2E_URL) await expect.poll(() => frames).toBeGreaterThan(frameAtMove);
	else {
		// Mocked route frames are not exposed as network websocket events. Trigger retained
		// LayerCake resize/repaint updates without moving the cursor instead.
		await page.setViewportSize({ width: 1180, height: 790 });
		await page.evaluate(() => new Promise<void>((resolve) => requestAnimationFrame(() => resolve())));
	}
	await expect(readout).toHaveText(moved!);
});
