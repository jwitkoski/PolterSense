<script lang="ts">
	import { onMount } from 'svelte';
	import Map from '$lib/Map.svelte';
	import BackgroundUpload from '$lib/BackgroundUpload.svelte';
	import InventoryEditor from '$lib/InventoryEditor.svelte';
	import { config } from '$lib/stores';
	import { loadConfig, request, saveYaml } from '$lib/api';
	import type { AppState, ConfigResponse } from '$lib/types';
	let scene: ConfigResponse | null = $state(null);
	let runtime: AppState | null = $state(null);
	let floorId = $state('');
	let tab: 'inventory' | 'diagnostics' | 'configuration' = $state('inventory');
	let error = $state('');
	let feed = $state('Connecting');
	let yaml = $state('');
	let yamlRevision = $state('');
	let yamlDirty = $state(false);
	let saving = $state(false);
	let saveMessage = $state('');
	let replaying = $state(false);
	let selection: { kind: 'wisps' | 'sources' | 'links'; id: string | null; key: number } | null = $state(null);
	let selectionKey = 0;
	function accept(next: ConfigResponse) {
		scene = next;
		config.set(next.config);
		if (!next.config.floors.some((f) => f.id === floorId)) floorId = next.config.floors[0]?.id ?? '';
		if (!yamlDirty) {
			yaml = next.yaml;
			yamlRevision = next.revision;
		}
		error = '';
	}
	function edit(kind: 'wisps' | 'sources' | 'links', id: string | null = null) {
		selection = { kind, id, key: ++selectionKey };
		tab = 'inventory';
	}
	async function refreshConfig() {
		try {
			accept(await loadConfig());
		} catch {
			error = 'Configuration could not be loaded. The last displayed scene remains available.';
		}
	}
	async function storeYaml() {
		saving = true;
		saveMessage = '';
		try {
			const next = await saveYaml(yamlRevision, yaml);
			yamlDirty = false;
			accept(next);
			saveMessage = 'Configuration saved. Map and inventory refreshed.';
		} catch (caught) {
			saveMessage = caught instanceof Error ? caught.message : 'Configuration could not be saved.';
		} finally {
			saving = false;
		}
	}
	async function replay() {
		replaying = true;
		try {
			await request('/api/demo/replay', { method: 'POST' });
		} catch (caught) {
			error = caught instanceof Error ? caught.message : 'Replay could not be started.';
		} finally {
			replaying = false;
		}
	}
	onMount(() => {
		let stopped = false;
		let socket: WebSocket | undefined;
		let reconnect: ReturnType<typeof setTimeout> | undefined;
		let refreshing = false;
		let polling = false;
		let lastMessageAt = 0;
		async function receive(next: AppState) {
			if (stopped) return;
			runtime = next;
			if (next.configRevision !== scene?.revision && !refreshing) {
				refreshing = true;
				await refreshConfig();
				refreshing = false;
			}
		}
		async function poll() {
			if (polling || stopped) return;
			polling = true;
			try {
				await receive(await request<AppState>('/api/state'));
				if (socket?.readyState !== WebSocket.OPEN) feed = 'Polling';
			} catch {
				if (!stopped) feed = 'Disconnected';
			} finally {
				polling = false;
			}
		}
		function connect() {
			if (stopped) return;
			socket = new WebSocket(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/ws`);
			socket.onopen = () => {
				feed = 'Live';
			};
			socket.onmessage = (event) => {
				lastMessageAt = Date.now();
				try {
					void receive(JSON.parse(event.data) as AppState);
				} catch {
					feed = 'Invalid state summary';
				}
			};
			socket.onerror = () => {
				feed = 'Reconnecting';
			};
			socket.onclose = () => {
				if (!stopped) {
					feed = 'Reconnecting';
					reconnect = setTimeout(connect, 3000);
				}
			};
		}
		void refreshConfig();
		void poll();
		connect();
		const interval = setInterval(() => {
			if (socket?.readyState !== WebSocket.OPEN || Date.now() - lastMessageAt > 5000) void poll();
		}, 3000);
		return () => {
			stopped = true;
			clearInterval(interval);
			clearTimeout(reconnect);
			socket?.close();
		};
	});
	let currentFloor = $derived.by(() => scene?.config.floors.find((f) => f.id === floorId));
</script>

<div class="app-shell">
	<header class="masthead">
		<div class="brand">
			<span class="brand-mark" aria-hidden="true">P<span>•</span></span>
			<div>
				<h1>PolterSense</h1>
				<p>LOCAL WI-FI SENSING FOUNDATION</p>
			</div>
		</div>
		<div class="connection"><span class:active={feed === 'Live'} class="status-dot"></span><span>{feed} UI</span><span class="divider">/</span><span>MQTT: {runtime?.mqtt.status ?? 'connecting'}</span></div>
	</header>
	{#if error}<p class="error" role="alert">{error}</p>{/if}
	{#if scene?.error || runtime?.configurationError}<p class="error" role="alert">Configuration needs attention. The last valid scene is retained. Check the local configuration file.</p>{/if}
	<section class="intro">
		<div>
			<p class="eyebrow">THE SCENE</p>
			<h2>{scene?.config.name ?? 'Your sensing space'}</h2>
			<p>Geometry and radio inventory, ready for future inference.</p>
		</div>
		<div class="inference-note">
			<span class="pill">Medium · not implemented</span>
			<p>Occupancy unknown · no estimated tracks</p>
		</div>
	</section>
	<div class="workspace">
		<section class="map-panel panel" aria-label="Building map">
			<div class="section-heading">
				<div>
					<h3>Floor plan</h3>
					<p class="muted">Absolute building frame · meters</p>
				</div>
				<label class="floor-select"
					>Floor<select bind:value={floorId} aria-label="Floor"
						>{#each scene?.config.floors ?? [] as floor}<option value={floor.id}>{floor.name} · {floor.elevation} m</option>{/each}</select
					></label
				>
			</div>
			<div class="map-stage"><BackgroundUpload /><Map {floorId} onselect={edit} /></div>
			<div class="map-caption"><span><i class="legend-wisp"></i> Wisp</span><span><i class="legend-source"></i> Source</span><span>– – Configured link</span></div>
			<p class="map-help">Drag to pan · Shift + scroll to zoom · Focus map for arrow / + / − / 0 keys · Ctrl/Cmd + C over map collects polygon points. A local background is a temporary browser reference.</p>
			<div class="room-status">
				<h4>{currentFloor?.rooms.length ?? 0} rooms · all estimates unknown</h4>
				{#each currentFloor?.rooms ?? [] as room}<span>{room.name} <small>unknown</small></span>{/each}
			</div>
			<p class="muted">Placements and floor membership are configuration, not coverage or motion locations.</p>
		</section>
		<section class="details-panel">
			<nav class="tabs" aria-label="Scene tools">
				{#each ['inventory', 'diagnostics', 'configuration'] as item}<button class:selected={tab === item} onclick={() => (tab = item as typeof tab)}>{item === 'configuration' ? 'YAML' : item[0].toUpperCase() + item.slice(1)}</button>{/each}
			</nav>
			{#if tab === 'inventory'}
				{#if selection && scene}{#key selection.key}<InventoryEditor {scene} kind={selection.kind} itemId={selection.id} onclose={() => (selection = null)} onsaved={accept} />{/key}{:else}
					<div class="panel inventory">
						<p class="eyebrow">CENTRAL INVENTORY</p>
						<p class="muted">Stable application IDs stay separate from display names and external firmware identities.</p>
						{#each ['wisps', 'sources', 'links'] as kind}
							<div class="inventory-group">
								<div class="section-heading">
									<h3>{kind === 'links' ? 'Sensing links' : kind === 'wisps' ? 'Wisps' : 'Sources'} <span class="count">{scene?.config[kind as 'wisps' | 'sources' | 'links'].length ?? 0}</span></h3>
									<button disabled={!scene} onclick={() => edit(kind as 'wisps' | 'sources' | 'links')}>Add {kind === 'links' ? 'link' : kind === 'wisps' ? 'Wisp' : 'Source'}</button>
								</div>
								{#each scene?.config[kind as 'wisps' | 'sources' | 'links'] ?? [] as item}
									<button class="inventory-item" onclick={() => edit(kind as 'wisps' | 'sources' | 'links', item.id)}
										><span><strong>{item.name}</strong><code>{item.id}</code></span><span class="inventory-meta"
											>{#if 'point' in item}{item.point.join(', ')} m{:else}{item.wispId} → {item.sourceId}{/if}<small>Edit ↗</small></span
										></button
									>
								{:else}<p class="empty-small">No {kind === 'links' ? 'sensing links' : kind} configured.</p>{/each}
							</div>
						{/each}
						<p class="muted">Receiver management is local inventory only. Firmware control and OTA are unavailable.</p>
					</div>
				{/if}
			{:else if tab === 'diagnostics'}
				<div class="panel diagnostics">
					<p class="eyebrow">OBSERVATION HEALTH</p>
					<h3>Evidence, before inference</h3>
					<p class="muted">Motion evidence belongs to a sensing link. It does not establish a room, occupancy, identity or position.</p>
					<div class="metrics">
						<div><strong data-testid="accepted-count">{runtime?.diagnostics.accepted ?? 0}</strong><span>accepted</span></div>
						<div><strong>{runtime?.diagnostics.rejected ?? 0}</strong><span>rejected</span></div>
						<div><strong>{runtime?.diagnostics.dropped ?? 0}</strong><span>dropped</span></div>
					</div>
					<dl class="facts">
						<dt>Medium</dt>
						<dd>Inference not implemented</dd>
						<dt>Input health</dt>
						<dd>{runtime?.medium.inputHealth ?? 'unknown'}</dd>
						<dt>MQTT connection</dt>
						<dd>{runtime?.mqtt.status ?? 'unknown'}</dd>
						<dt>Test input</dt>
						<dd>{runtime?.mqtt.testInputEnabled ? 'Explicitly enabled' : 'Disabled'}</dd>
						<dt>Stale / duplicates / out of order</dt>
						<dd>{runtime?.diagnostics.stale ?? 0} / {runtime?.diagnostics.duplicate ?? 0} / {runtime?.diagnostics.outOfOrder ?? 0}</dd>
						<dt>Retained input</dt>
						<dd>{runtime?.diagnostics.retained ?? 0}</dd>
					</dl>
					<p class="muted">A disconnected transport or absence of motion is never proof of vacancy.</p>
					{#if runtime?.diagnostics.lastRejection}<p class="notice">Last rejection: {runtime.diagnostics.lastRejection}</p>{/if}
					{#if runtime?.demoEnabled}<button class="primary" onclick={replay} disabled={replaying}>{replaying ? 'Replaying…' : 'Replay synthetic Echo'}</button>
						<p class="muted">Fictional recorded scene. No operational outputs.</p>{/if}
					<h4>Latest observation summaries</h4>
					{#each runtime?.diagnostics.latest ?? [] as observation}
						<article class="observation">
							<div class="section-heading"><strong>{observation.wispId}</strong><span class="pill">{observation.provenance.origin}</span></div>
							<p>{observation.linkId ?? 'Unknown link'} · source {observation.sourceId ?? 'unknown'}</p>
							<p>Motion {observation.motion === true ? 'reported' : observation.motion === false ? 'not reported' : 'unknown'} · score {observation.score ?? 'unknown'} · {observation.ready === true ? 'ready' : observation.ready === false ? 'not ready' : 'readiness unknown'}</p>
							<p>Freshness: {observation.freshness} · clock {observation.clockBasis ?? 'unknown'}</p>
							<p class="muted">Adapter {observation.provenance.adapter} · references {observation.provenance.referenceOrigin} · radio metadata {observation.provenance.radioOrigin}</p>
							<small>Received {observation.receivedAt}<br />Acquired {observation.acquiredAt ?? 'unknown — arrival time is not acquisition time'}<br />Scene {observation.configRevision.slice(0, 12)}{observation.configRevision !== scene?.revision ? ' · recorded scene differs from displayed scene' : ''}</small>
						</article>
					{:else}<p class="empty-small">No accepted observations. The scene remains available without a broker or receivers.</p>{/each}
				</div>
			{:else}
				<div class="panel">
					<p class="eyebrow">AUTHORITATIVE CONFIGURATION</p>
					<h3>Scene YAML</h3>
					<p class="muted">Floor bounds, room polygons and inventory share one validated configuration. Coordinates are absolute meters, including floor elevations. Secrets belong in environment variables, never here.</p>
					<label
						>Configuration YAML<textarea
							class="yaml"
							bind:value={yaml}
							oninput={() => {
								yamlDirty = true;
								saveMessage = '';
							}}
							spellcheck="false"
							aria-label="Configuration YAML"
						></textarea></label
					>
					<p class="muted">Draft revision {yamlRevision.slice(0, 12) || 'unavailable'}{yamlDirty ? ' · unsaved changes' : ''}</p>
					{#if yamlDirty && scene?.revision !== yamlRevision}<p class="notice">The scene changed while this draft was open. Copy your edits, then reload current YAML before saving.</p>{/if}
					<div class="actions">
						<button class="primary" disabled={saving || !scene} onclick={storeYaml}>{saving ? 'Saving…' : 'Save YAML'}</button><button
							disabled={!scene}
							onclick={() => {
								if (scene) {
									yaml = scene.yaml;
									yamlRevision = scene.revision;
									yamlDirty = false;
									saveMessage = 'Current scene loaded into editor.';
								}
							}}>Reload current YAML</button
						>
					</div>
					{#if saveMessage}<p class="notice" role="status">{saveMessage}</p>{/if}
				</div>
			{/if}
		</section>
	</div>
	<footer><span>PolterSense · experimental foundation</span><span>Scene revision <code>{scene?.revision.slice(0, 12) ?? 'unavailable'}</code></span><span>Built on ESPresense Companion infrastructure</span></footer>
</div>
