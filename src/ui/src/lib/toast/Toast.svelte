<script lang="ts">
	import { toastStore } from './toastStore';
</script>

<div class="toasts" aria-live="polite">
	{#each $toastStore as toast (toast.id)}<div class="toast"><span>{toast.message}</span><button onclick={() => toastStore.close(toast.id)} aria-label="Close notification">×</button></div>{/each}
</div>
