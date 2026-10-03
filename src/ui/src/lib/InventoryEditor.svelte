<script lang="ts">
	import { untrack } from 'svelte';
	import { saveConfig } from './api';
	import type { ConfigResponse, Point, Wisp, Source, SensingLink } from './types';
	let { scene, kind, itemId, onclose, onsaved }: { scene: ConfigResponse; kind: 'wisps' | 'sources' | 'links'; itemId: string | null; onclose: () => void; onsaved: (scene: ConfigResponse) => void } = $props();
	// Capture a revision with this draft: live refresh must not silently rebase unsaved edits.
	const base = untrack(() => $state.snapshot(scene));
	const original = untrack(() => base.config[kind].find((item) => item.id === itemId));
	let id = $state(original?.id ?? '');
	let name = $state(original?.name ?? '');
	const placed = original && 'point' in original ? original : undefined;
	const radio = untrack(() => (kind === 'sources' ? (original as Source | undefined) : undefined));
	const link = original && 'wispId' in original ? original : undefined;
	let point: Point = $state(placed ? [...placed.point] : [0, 0, 0]);
	let floors: string[] = $state(placed ? [...placed.floors] : []);
	let externalId = $state(original && 'externalId' in original ? (original.externalId ?? '') : '');
	let apLabel = $state(radio?.apLabel ?? '');
	let radioId = $state(radio?.radioId ?? '');
	let bssid = $state(radio?.bssid ?? '');
	let channel: number | undefined = $state(radio?.channel ?? undefined);
	let wispId = $state(link?.wispId ?? '');
	let sourceId = $state(link?.sourceId ?? '');
	let error = $state('');
	let busy = $state(false);
	const singular = $derived(kind === 'wisps' ? 'Wisp' : kind === 'sources' ? 'Source' : 'Sensing link');
	function optional(value: string) {
		return value.trim() || undefined;
	}
	async function persist(remove = false) {
		error = '';
		busy = true;
		try {
			const config = structuredClone(base.config);
			if (remove) {
				if (kind === 'wisps') config.wisps = config.wisps.filter((item) => item.id !== itemId);
				else if (kind === 'sources') config.sources = config.sources.filter((item) => item.id !== itemId);
				else config.links = config.links.filter((item) => item.id !== itemId);
			} else {
				if (!id.trim() || !name.trim()) throw new Error('A stable ID and display name are required.');
				if (kind === 'links') {
					const item: SensingLink = { id: id.trim(), name: name.trim(), wispId, sourceId };
					config.links = itemId ? config.links.map((old) => (old.id === itemId ? item : old)) : [...config.links, item];
				} else {
					if (point.some((value) => !Number.isFinite(value))) throw new Error('All three coordinates must be numbers in meters.');
					if (kind === 'wisps') {
						const item: Wisp = { id: id.trim(), name: name.trim(), externalId: optional(externalId), point: [...point], floors: [...floors], orientation: placed?.orientation };
						config.wisps = itemId ? config.wisps.map((old) => (old.id === itemId ? item : old)) : [...config.wisps, item];
					} else {
						const item: Source = { id: id.trim(), name: name.trim(), apLabel: optional(apLabel), radioId: optional(radioId), bssid: optional(bssid), channel: channel, point: [...point], floors: [...floors], orientation: placed?.orientation };
						config.sources = itemId ? config.sources.map((old) => (old.id === itemId ? item : old)) : [...config.sources, item];
					}
				}
			}
			onsaved(await saveConfig(base.revision, config));
			onclose();
		} catch (caught) {
			error = caught instanceof Error ? caught.message : 'Could not save configuration.';
		} finally {
			busy = false;
		}
	}
</script>

<form
	class="editor panel"
	onsubmit={(event) => {
		event.preventDefault();
		void persist();
	}}
>
	<div class="section-heading">
		<h3>{itemId ? 'Edit' : 'Add'} {singular}</h3>
		<button type="button" onclick={onclose} aria-label="Close inventory editor">×</button>
	</div>
	<p class="muted">Local configuration only. No firmware commands or radio settings are sent.</p>
	<div class="form-grid">
		<label>Stable ID<input bind:value={id} required readonly={!!itemId} maxlength="80" placeholder={kind === 'wisps' ? 'wisp-study' : 'stable-id'} /></label>
		<label>Display name<input bind:value={name} required maxlength="120" /></label>
		{#if kind === 'links'}
			<label
				>Wisp<select bind:value={wispId} required
					><option value="">Choose a Wisp</option>{#each base.config.wisps as item}<option value={item.id}>{item.name} ({item.id})</option>{/each}</select
				></label
			>
			<label
				>Source<select bind:value={sourceId} required
					><option value="">Choose a Source</option>{#each base.config.sources as item}<option value={item.id}>{item.name} ({item.id})</option>{/each}</select
				></label
			>
		{:else}
			{#if kind === 'wisps'}<label>External firmware identity (optional)<input bind:value={externalId} maxlength="160" placeholder="Unknown / not mapped" /></label>{:else}
				<label>Physical AP label (optional)<input bind:value={apLabel} maxlength="120" /></label>
				<label>Configured radio identity (optional)<input bind:value={radioId} maxlength="160" placeholder="Unknown" /></label>
				<label>BSSID (optional)<input bind:value={bssid} maxlength="17" placeholder="Unknown" /></label>
				<label>Channel (optional)<input bind:value={channel} type="number" min="1" max="233" step="1" placeholder="Unknown" /></label>
			{/if}
			<fieldset class="coordinates">
				<legend>Absolute building coordinates · meters</legend>{#each ['X', 'Y', 'Z'] as axis, index}<label>{axis}<input aria-label={`${axis} coordinate`} type="number" step="any" bind:value={point[index]} required /></label>{/each}
			</fieldset>
			<fieldset>
				<legend>Floor membership (metadata)</legend>{#each base.config.floors as floor}<label class="checkbox"><input type="checkbox" value={floor.id} bind:group={floors} />{floor.name}</label>{/each}
			</fieldset>
		{/if}
	</div>
	{#if error}<p class="error" role="alert">{error} Your draft is preserved.</p>{/if}
	<div class="actions">
		<button class="primary" disabled={busy} type="submit">{busy ? 'Saving…' : 'Save configuration'}</button><button type="button" onclick={onclose}>Cancel</button>{#if itemId}<button type="button" disabled={busy} onclick={() => persist(true)}>Remove {singular}</button>{/if}
	</div>
	<p class="muted">Revision {base.revision.slice(0, 12)}. Referenced inventory items cannot be removed until their links are removed.</p>
</form>
