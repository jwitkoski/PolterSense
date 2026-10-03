<script lang="ts">
	import { onMount } from 'svelte';
	import { request, saveConfig } from './api';
	import type { ConfigResponse, EspectreDiscovery, EspectreSnapshot, EspectreUpdate, Wisp } from './types';
	let { scene, onsaved }: { scene: ConfigResponse; onsaved: (next: ConfigResponse) => void } = $props();
	let address = $state('');
	let port = $state(62587);
	let candidates: EspectreSnapshot[] = $state([]);
	let discoveryMessage = $state('');
	let busy = $state(false);
	let message = $state('');
	let selected: EspectreSnapshot | null = $state(null);
	let draftRevision = $state('');
	let base: ConfigResponse | null = $state(null);
	let bindId = $state('');
	let newId = $state('');
	let newName = $state('');
	let point: [number | undefined, number | undefined, number | undefined] = $state([undefined, undefined, undefined]);
	let floors: string[] = $state([]);
	let label = $state('');
	let enabled = $state(false);
	let detector = $state('');
	let threshold = $state(0);
	let motionOnHits = $state(1);
	let motionOffHits = $state(1);
	let trafficGeneratorMode = $state('');
	const managed = $derived(scene.config.wisps.filter((wisp) => wisp.directHttp && wisp.externalId));
	const mapped = $derived(selected ? scene.config.wisps.find((wisp) => wisp.externalId === selected?.device.deviceId && wisp.directHttp?.address === selected?.address && wisp.directHttp?.port === selected?.port) : undefined);
	const assigned = $derived(selected ? scene.config.wisps.find((wisp) => wisp.externalId === selected?.device.deviceId) : undefined);
	const configChanged = $derived(!!draftRevision && draftRevision !== scene.revision);
	function choose(snapshot: EspectreSnapshot, revision = scene.revision) {
		selected = snapshot;
		draftRevision = revision;
		base = $state.snapshot(scene);
		bindId = '';
		newId = '';
		newName = '';
		point = [undefined, undefined, undefined];
		floors = [];
		label = snapshot.device.label ?? '';
		if (snapshot.sensing) ({ enabled, detector, threshold, motionOnHits, motionOffHits, trafficGeneratorMode } = snapshot.sensing);
		message = '';
	}
	async function discover(seed = false) {
		busy = true;
		discoveryMessage = 'Looking for ESPectre receivers…';
		try {
			const result = await request<EspectreDiscovery>('/api/wisps/discover', { method: 'POST', signal: AbortSignal.timeout(35000), body: JSON.stringify(seed ? { address: address.trim(), port } : {}) });
			candidates = result.candidates;
			discoveryMessage = result.message;
		} catch {
			discoveryMessage = 'Discovery could not complete. Check the receiver address, Direct HTTP support and network reachability.';
		} finally {
			busy = false;
		}
	}
	async function refresh(wisp: Wisp) {
		busy = true;
		message = '';
		const revision = scene.revision;
		try {
			choose(await request<EspectreSnapshot>(`/api/wisps/${encodeURIComponent(wisp.id)}/espectre`, { signal: AbortSignal.timeout(35000) }), revision);
		} catch {
			message = 'The receiver could not be read. Check its address, identity and Direct HTTP support. Your draft is preserved.';
		} finally {
			busy = false;
		}
	}
	async function bind() {
		if (!selected || !base) return;
		busy = true;
		message = '';
		try {
			const config = structuredClone($state.snapshot(base.config));
			const identity = { externalId: selected.device.deviceId, directHttp: { address: selected.address, port: selected.port } };
			if (bindId === 'new') {
				if (!newId.trim() || !newName.trim() || point.some((value) => value === undefined || !Number.isFinite(value))) {
					message = 'Supply a stable Wisp ID, display name and all three placement coordinates in meters.';
					return;
				}
				config.wisps.push({ id: newId.trim(), name: newName.trim(), point: point as [number, number, number], floors: [...floors], ...identity });
			} else {
				if (!config.wisps.some((wisp) => `existing:${wisp.id}` === bindId)) return;
				config.wisps = config.wisps.map((wisp) => (`existing:${wisp.id}` === bindId ? { ...wisp, ...identity } : wisp));
			}
			const next = await saveConfig(base.revision, config);
			onsaved(next);
			base = next;
			draftRevision = next.revision;
			message = 'Receiver mapped in local configuration. No device settings were changed.';
		} catch {
			message = 'Mapping could not be saved. Check the stable ID, geometry and current scene revision. Your draft is preserved.';
		} finally {
			busy = false;
		}
	}
	async function apply(kind: 'label' | 'sensing') {
		if (!mapped || !selected) return;
		if (kind === 'label' && (new TextEncoder().encode(label).length > 32 || /[\r\n]/.test(label))) {
			message = 'Firmware labels must be a single line of at most 32 UTF-8 bytes.';
			return;
		}
		busy = true;
		message = '';
		try {
			const result = await request<EspectreUpdate>(`/api/wisps/${encodeURIComponent(mapped.id)}/espectre`, {
				method: 'PATCH',
				signal: AbortSignal.timeout(35000),
				body: JSON.stringify({ revision: draftRevision, expectedDeviceId: selected.device.deviceId, ...(kind === 'label' ? { label } : { sensing: { enabled, detector, threshold, motionOnHits, motionOffHits, trafficGeneratorMode } }) })
			});
			if (result.confirmed && result.snapshot) {
				selected = result.snapshot;
				if (kind === 'label') label = result.snapshot.device.label ?? '';
				else if (result.snapshot.sensing) ({ enabled, detector, threshold, motionOnHits, motionOffHits, trafficGeneratorMode } = result.snapshot.sensing);
				message = 'Device settings confirmed by readback. Local Wisp name and placement are unchanged.';
			} else message = `${result.message} Changes were not confirmed; your draft is preserved. Refresh the receiver before retrying.`;
		} catch {
			message = 'Device settings were not confirmed. Your draft is preserved. Refresh the receiver before retrying; some settings may already have reached it.';
		} finally {
			busy = false;
		}
	}
	onMount(() => {
		void discover();
	});
</script>

<section class="panel espectre" aria-label="Wisp discovery and setup">
	<p class="eyebrow">ESPECTRE RECEIVERS</p>
	<h3>Discover and set up Wisps</h3>
	<p class="muted">Read receiver identity and capabilities over Direct HTTP. Discovery does not determine placement, radio coverage or occupancy. Device settings are sent only with an explicit Apply action.</p>
	<div class="actions"><button disabled={busy} onclick={() => discover()}>Discover receivers</button></div>
	<form
		onsubmit={(event) => {
			event.preventDefault();
			void discover(true);
		}}
	>
		<div class="form-grid">
			<label>Receiver IPv4 address<input bind:value={address} placeholder="192.168.1.50" required maxlength="15" /></label>
			<label>Direct HTTP port<input type="number" bind:value={port} required min="1" max="65535" step="1" /></label>
		</div>
		<button disabled={busy} type="submit">Discover by address</button>
	</form>
	<p class="muted">Automatic discovery depends on mDNS visibility. Across VLANs or from a bridged container, use the receiver address. Previously mapped endpoints are also checked.</p>
	{#if discoveryMessage}<p class="notice" role="status">{discoveryMessage}</p>{/if}
	{#each candidates as candidate}
		<button class="candidate" disabled={busy} onclick={() => choose(candidate)}><strong>{candidate.device.label || candidate.device.name}</strong><span>{candidate.device.deviceId} · {candidate.address}:{candidate.port}</span><small>Firmware {candidate.device.firmware} · {candidate.device.frontend}</small></button>
	{/each}
	{#if managed.length}
		<h4>Mapped receivers</h4>
		{#each managed as wisp}<button class="candidate" disabled={busy} onclick={() => refresh(wisp)}><strong>Read {wisp.name}</strong><span>{wisp.externalId} · {wisp.directHttp?.address}:{wisp.directHttp?.port}</span></button>{/each}
	{/if}
	{#if selected}
		<hr />
		<h3>{selected.device.label || selected.device.name}</h3>
		<dl class="facts">
			<dt>External identity</dt>
			<dd>{selected.device.deviceId}</dd>
			<dt>Firmware / chip</dt>
			<dd>{selected.device.firmware} / {selected.device.chip}</dd>
			<dt>Frontend / protocol</dt>
			<dd>{selected.device.frontend} / {selected.capabilities.protocolVersion}</dd>
			<dt>Sensing readiness</dt>
			<dd>{selected.sensing ? (selected.sensing.ready ? 'Ready' : 'Not ready') : 'Unavailable'}{selected.sensing?.calibrating ? ' · calibrating' : ''}</dd>
			<dt>Mode / derived events</dt>
			<dd>{selected.sensing?.mode ?? 'unavailable'} / {selected.sensing ? (selected.sensing.derivedEventsPaused ? 'paused' : 'not paused') : 'unknown'}</dd>
			<dt>Last read</dt>
			<dd>{selected.readAt}</dd>
		</dl>
		<p class="muted">Readiness describes the receiver, not an occupancy estimate. These settings do not ingest motion observations.</p>
		{#if configChanged}<p class="notice">The scene changed while this draft was open. Read the receiver or select a discovery result again before saving.</p>{/if}
		{#if !mapped}
			<form
				onsubmit={(event) => {
					event.preventDefault();
					void bind();
				}}
			>
				<h4>Map to local inventory</h4>
				<label
					>Local Wisp<select bind:value={bindId} required
						><option value="">Choose an existing Wisp or create one</option><option value="new" disabled={!!assigned}>Create a new Wisp</option>{#each base?.config.wisps ?? [] as wisp}<option value={`existing:${wisp.id}`} disabled={!!assigned && assigned.id !== wisp.id}>{wisp.name} ({wisp.id})</option>{/each}</select
					></label
				>
				{#if bindId === 'new'}
					<div class="form-grid">
						<label>New Wisp stable ID<input bind:value={newId} required maxlength="80" /></label>
						<label>New Wisp display name<input bind:value={newName} required maxlength="120" /></label>
						<fieldset class="coordinates">
							<legend>Explicit placement · absolute meters</legend>{#each ['X', 'Y', 'Z'] as axis, index}<label>{axis}<input aria-label={`New Wisp ${axis} coordinate`} type="number" step="any" bind:value={point[index]} required /></label>{/each}
						</fieldset>
						<fieldset>
							<legend>Floor membership (metadata)</legend>{#each base?.config.floors ?? [] as floor}<label class="checkbox"><input type="checkbox" value={floor.id} bind:group={floors} />{floor.name}</label>{/each}
						</fieldset>
					</div>
				{/if}
				<p class="muted">This saves the reported identity and endpoint locally. Existing Wisp names and placements are preserved; previous external bindings are replaced.</p>
				<button class="primary" type="submit" disabled={busy || !bindId || configChanged}>Save local mapping</button>
			</form>
		{:else}
			<h4>Device settings for {mapped.name}</h4>
			<p class="notice">Apply sends settings to this physical receiver. Readback is required before success is reported.</p>
			<form
				onsubmit={(event) => {
					event.preventDefault();
					void apply('label');
				}}
			>
				<label>Firmware label<input bind:value={label} maxlength="32" disabled={!selected.capabilities.canUpdateDevice} /></label>
				<p class="muted">Independent of the local Wisp display name. Single line, at most 32 UTF-8 bytes. {selected.capabilities.canUpdateDevice ? '' : 'Label updates unsupported.'}</p>
				<button type="submit" disabled={busy || configChanged || !selected.capabilities.canUpdateDevice}>Apply label to device</button>
			</form>
			<form
				onsubmit={(event) => {
					event.preventDefault();
					void apply('sensing');
				}}
			>
				<fieldset disabled={!selected.capabilities.canUpdateSensing || !selected.sensing}>
					<legend>Sensing settings {selected.capabilities.canUpdateSensing && selected.sensing ? '' : '· updates unavailable'}</legend>
					<label class="checkbox"><input type="checkbox" bind:checked={enabled} />Sensing enabled</label>
					<div class="form-grid">
						<label
							>Detector<select bind:value={detector} required
								><option value="lightweight">Lightweight</option><option value="high_accuracy">High accuracy</option>{#if detector && !['lightweight', 'high_accuracy'].includes(detector)}<option value={detector}>{detector} (reported)</option>{/if}</select
							></label
						>
						<label>Threshold<input type="number" min="0" max="1" step="any" bind:value={threshold} required /></label>
						<label>Motion-on hits<input type="number" step="1" min="1" max="20" bind:value={motionOnHits} required /></label>
						<label>Motion-off hits<input type="number" step="1" min="1" max="20" bind:value={motionOffHits} required /></label>
						<label
							>Traffic generator mode<select bind:value={trafficGeneratorMode} required
								>{#each ['ping', 'dns', 'dns_tcp', 'wifi_raw', 'external'] as mode}<option value={mode}>{mode}{mode === 'wifi_raw' ? ' (experimental)' : ''}</option>{/each}{#if trafficGeneratorMode && !['ping', 'dns', 'dns_tcp', 'wifi_raw', 'external'].includes(trafficGeneratorMode)}<option value={trafficGeneratorMode}>{trafficGeneratorMode} (reported)</option>{/if}</select
							></label
						>
					</div>
				</fieldset>
				<p class="muted">Traffic generation affects the measurement setup. Unsupported field values will be rejected by the adapter or receiver.</p>
				<button class="primary" type="submit" disabled={busy || configChanged || !selected.capabilities.canUpdateSensing || !selected.sensing}>Apply sensing to device</button>
			</form>
			<button disabled={busy} onclick={() => mapped && refresh(mapped)}>Refresh device (replace draft)</button>
		{/if}
		<p class="muted">OTA, Wi-Fi/MQTT configuration and recalibration are unavailable here.</p>
	{/if}
	{#if message}<p class="notice" role="status">{message}</p>{/if}
</section>

<style>
	form {
		margin: 16px 0;
	}
	.espectre {
		overflow-wrap: anywhere;
	}
	.candidate {
		display: grid;
		width: 100%;
		text-align: left;
		gap: 4px;
		margin-top: 10px;
	}
	.candidate span {
		color: var(--muted);
		font-size: 11px;
	}
	hr {
		border: 0;
		border-top: 1px solid var(--line);
		margin: 22px 0;
	}
</style>
