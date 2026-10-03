import type { ConfigResponse } from '../src/lib/types';
export function fixture(): ConfigResponse {
	return {
		revision: 'fixture-scene-1',
		yaml: 'schema_version: 1\nname: Fictional test house\n',
		config: {
			schemaVersion: 1,
			name: 'Fictional test house',
			map: { flipX: false, flipY: true, wallThickness: 0.1, wallOpacity: 0.8 },
			floors: [
				{
					id: 'lower',
					name: 'Lower level',
					elevation: 0,
					bounds: [
						[0, 0, 0],
						[8, 6, 3]
					],
					rooms: [
						{
							id: 'study',
							name: 'Study',
							points: [
								[0, 0],
								[4, 0],
								[4, 6],
								[0, 6]
							]
						}
					]
				},
				{
					id: 'upper',
					name: 'Upper level',
					elevation: 3,
					bounds: [
						[0, 0, 3],
						[8, 6, 6]
					],
					rooms: [
						{
							id: 'bedroom',
							name: 'Bedroom',
							points: [
								[0, 0],
								[8, 0],
								[8, 6],
								[0, 6]
							]
						}
					]
				}
			],
			wisps: [{ id: 'wisp-one', name: 'Study Wisp', externalId: 'fictional-hardware', point: [1, 1, 1], floors: ['lower'] }],
			sources: [
				{ id: 'source-one', name: 'Hall Source', point: [6, 3, 2], floors: ['lower'] },
				{ id: 'source-two', name: 'Upper Source', point: [6, 3, 4], floors: ['upper'] }
			],
			links: [{ id: 'link-one', name: 'Study link', wispId: 'wisp-one', sourceId: 'source-one' }]
		}
	};
}
