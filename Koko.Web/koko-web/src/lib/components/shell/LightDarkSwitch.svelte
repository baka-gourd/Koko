<script lang="ts">
    import { Button, Theme } from "carbon-components-svelte";
    import type { CarbonTheme } from "carbon-components-svelte/src/Theme/Theme.svelte";

    let theme: CarbonTheme = $state("g100");
    import LightFilled from "carbon-icons-svelte/lib/LightFilled.svelte";
</script>

<Theme bind:theme persist persistKey="__carbon-theme" />

<Button icon={LightFilled}></Button>
