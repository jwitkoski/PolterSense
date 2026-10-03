import { test, expect } from '@playwright/test';
import { prepareMap } from './map-fixture';

test('map renders with LayerCake at useful dimensions', async ({ page }) => {
	await prepareMap(page);
	await page.setViewportSize({ width: 1200, height: 800 });
	await page.goto('/');
	const svg = page.locator('.map-canvas svg');
	await expect(svg).toBeVisible();
	await expect(async () => {
		const box = await svg.boundingBox();
		expect(box?.width).toBeGreaterThan(100);
		expect(box?.height).toBeGreaterThan(100);
	}).toPass();
	await expect(page.locator('.map-canvas .x-axis')).toBeVisible();
	await expect(page.locator('.map-canvas .y-axis')).toBeVisible();
});
