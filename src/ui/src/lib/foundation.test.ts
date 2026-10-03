import { afterEach, describe, expect, it, vi } from 'vitest';
import { squareBounds } from './geometry';
import { getRoomColor } from './colors';
import { isEditableTarget } from './dom';
import { request, saveConfig, saveYaml } from './api';
import type { Config } from './types';
const config: Config = {
	schemaVersion: 1,
	name: 'Fictional test',
	map: { flipX: false, flipY: true, wallThickness: 0.1, wallOpacity: 0.7 },
	floors: [
		{
			id: 'level',
			name: 'Level',
			elevation: 3,
			bounds: [
				[-5, 2, 3],
				[1, 5, 6]
			],
			rooms: [
				{
					id: 'room',
					name: 'Room',
					points: [
						[-5, 2],
						[1, 2],
						[1, 5]
					],
					color: '#abc'
				}
			]
		}
	],
	wisps: [],
	sources: [],
	links: []
};
afterEach(() => vi.unstubAllGlobals());
describe('retained geometry and map helpers', () => {
	it('keeps negative/offset origins and meter aspect ratio', () => {
		expect(squareBounds(config.floors[0].bounds)).toEqual([
			[-5, 2, 3],
			[1, 8, 6]
		]);
		expect(config.floors[0].bounds[1]).toEqual([1, 5, 6]);
	});
	it('respects configured colors and keeps unknown room colors stable', () => {
		expect(getRoomColor(config, 'room')).toBe('#AABBCC');
		expect(getRoomColor(config, 'unknown')).toBe(getRoomColor(null, 'unknown'));
	});
	it('does not treat text input as a map shortcut target', () => {
		for (const tag of ['input', 'textarea', 'select']) expect(isEditableTarget(document.createElement(tag))).toBe(true);
		expect(isEditableTarget(document.createElement('div'))).toBe(false);
		expect(isEditableTarget(null)).toBe(false);
	});
});
describe('authoritative configuration requests', () => {
	it('sends revision for both structured and YAML writes', async () => {
		const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: async () => ({ revision: 'next' }) });
		vi.stubGlobal('fetch', fetchMock);
		await saveConfig('original', config);
		expect(fetchMock.mock.calls[0][0]).toBe('/api/config');
		expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual({ revision: 'original', config });
		await saveYaml('original', 'schema_version: 1');
		expect(JSON.parse(fetchMock.mock.calls[1][1].body)).toEqual({ revision: 'original', yaml: 'schema_version: 1' });
	});
	it('reports conflicts without leaking arbitrary server response bodies', async () => {
		const json = vi.fn().mockResolvedValue({ error: 'secret-value' });
		vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 409, json }));
		await expect(request('/api/config')).rejects.toThrow('Configuration changed');
		expect(json).not.toHaveBeenCalled();
	});
	it('preserves validation failures as failures', async () => {
		vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 400 }));
		await expect(saveYaml('original', 'invalid')).rejects.toThrow('Check references');
	});
});
