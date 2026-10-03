<script lang="ts">
	import { getContext } from 'svelte';
	import { config } from './stores';
	import type { LayerCakeContext } from './types';
	let { floorId, onselect }: { floorId: string; onselect?: (kind: 'wisps' | 'sources', id: string) => void } = $props();
	const { xScale, yScale } = getContext<LayerCakeContext>('LayerCake');
</script>

{#each $config?.links ?? [] as link}
	{@const wisp = $config?.wisps.find((w) => w.id === link.wispId)}
	{@const source = $config?.sources.find((s) => s.id === link.sourceId)}
	{#if wisp && source && wisp.floors.includes(floorId) && source.floors.includes(floorId)}
		<line x1={$xScale(wisp.point[0])} y1={$yScale(wisp.point[1])} x2={$xScale(source.point[0])} y2={$yScale(source.point[1])} stroke="#75918c" stroke-dasharray="4 5" opacity="0.6"><title>Configured sensing link: {link.name}. This line is not a coverage estimate.</title></line>
	{/if}
{/each}
{#each ['wisps', 'sources'] as kind}
	{#each $config?.[kind as 'wisps' | 'sources'].filter((item) => item.floors.includes(floorId)) ?? [] as item}
		<g
			role="button"
			tabindex="0"
			aria-label={`${kind === 'wisps' ? 'Wisp' : 'Source'} ${item.name}`}
			onclick={() => onselect?.(kind as 'wisps' | 'sources', item.id)}
			onkeydown={(event) => {
				if (event.key === 'Enter' || event.key === ' ') onselect?.(kind as 'wisps' | 'sources', item.id);
			}}
			transform={`translate(${$xScale(item.point[0])},${$yScale(item.point[1])})`}
		>
			<rect x="-22" y="-26" width="44" height="48" fill="transparent" />
			<title>{item.name} · {item.id} · z={item.point[2]} m</title>
			{#if kind === 'wisps'}<circle r="6" fill="#86e1c4" stroke="#132b2a" stroke-width="2" />{:else}<path d="M0,-7 L7,0 L0,7 L-7,0 Z" fill="#efbd7d" stroke="#35291a" stroke-width="2" />{/if}
			<text y="-12" text-anchor="middle" fill="#e5eeed" font-size="11">{item.name}</text>
		</g>
	{/each}
{/each}
