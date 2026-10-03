<script lang="ts">
	import '../app.css';
	import Toast from '$lib/toast/Toast.svelte';
	let { children } = $props();
</script>

<svelte:head><title>PolterSense · Local sensing foundation</title><meta name="description" content="Local Wi-Fi sensing inventory, house geometry and observation diagnostics. Inference is not implemented." /></svelte:head>
{@render children()}<Toast />
