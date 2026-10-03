<script lang="ts">
	import { isEditableTarget } from './dom';
	import { LayerCake, Svg } from 'layercake';
	import { config } from './stores';
	import { select } from 'd3-selection';
	import { zoom, zoomIdentity } from 'd3-zoom';
	import Rooms from './Rooms.svelte';
	import AxisX from './AxisX.svelte';
	import AxisY from './AxisY.svelte';
	import MapCoordinates from './MapCoordinates.svelte';
	import InventoryMarkers from './InventoryMarkers.svelte';
	import { squareBounds } from './geometry';
	let { floorId, onselect }: { floorId: string; onselect?: (kind: 'wisps' | 'sources', id: string) => void } = $props();
	let svg: SVGElement | undefined = $state();
	let transform = $state(zoomIdentity);
	let floor = $derived($config?.floors.find((f) => f.id === floorId));
	let bounds = $derived(floor ? squareBounds(floor.bounds) : undefined);
	const handler = zoom<SVGElement, unknown>()
		.scaleExtent([0.5, 40])
		.wheelDelta((event) => (event.shiftKey ? -(event.deltaY + event.deltaX * 0.25) * 0.002 : 0))
		.on('zoom', (e) => {
			transform = e.transform;
		});
	function handleKeyboard(event: KeyboardEvent) {
		if (isEditableTarget(event.target)) return;
		const amount = event.shiftKey ? 5 : 50;
		let next = transform;
		switch (event.key) {
			case '0':
				next = zoomIdentity;
				break;
			case '+':
			case '=':
				next = transform.scale(1.1);
				break;
			case '-':
				next = transform.scale(1 / 1.1);
				break;
			case 'ArrowLeft':
				next = transform.translate(-amount / transform.k, 0);
				break;
			case 'ArrowRight':
				next = transform.translate(amount / transform.k, 0);
				break;
			case 'ArrowUp':
				next = transform.translate(0, -amount / transform.k);
				break;
			case 'ArrowDown':
				next = transform.translate(0, amount / transform.k);
				break;
		}
		if (svg && next !== transform) {
			event.preventDefault();
			select(svg).call(handler.transform, next);
		}
	}
	function reset() {
		if (svg) select(svg).call(handler.transform, zoomIdentity);
	}
	function range({ height, width }: { height: number; width: number }, flip: boolean) {
		const size = Math.min(height, width);
		return flip ? [size, 0] : [0, size];
	}
	$effect(() => {
		if (svg) select(svg).call(handler);
	});
	$effect(() => {
		void floorId;
		reset();
	});
</script>

<!-- Keyboard tools are scoped to the focused map, so page and form navigation remain available. -->
<!-- svelte-ignore a11y_no_noninteractive_tabindex, a11y_no_noninteractive_element_interactions (This focusable map region implements documented keyboard pan/zoom controls.) -->
<div class="map-canvas" role="region" tabindex="0" aria-label="Floor plan; arrow keys pan, plus and minus zoom, zero resets" onkeydown={handleKeyboard}>
	{#if bounds}
		<LayerCake x="0" y="1" xRange={(size: { height: number; width: number }) => range(size, $config?.map.flipX ?? false)} yRange={(size: { height: number; width: number }) => range(size, $config?.map.flipY ?? false)} flatData={bounds} padding={{ top: 34, left: 32, bottom: 32, right: 24 }}>
			<Svg bind:element={svg}>
				<AxisX {transform} /><AxisY {transform} /><Rooms {transform} {floorId} />
				<g transform={transform.toString()}><InventoryMarkers {floorId} {onselect} /></g>
				<MapCoordinates {transform} />
			</Svg>
		</LayerCake>
	{:else}<div class="empty">No floor configured. Add a floor and room polygons in the YAML editor.</div>{/if}
</div>
<button class="reset-map" onclick={reset}>Reset view</button>
