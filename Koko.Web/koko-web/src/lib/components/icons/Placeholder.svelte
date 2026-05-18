<script>
    let { size = 16, title = undefined, ...restProps } = $props();

    let labelled = $derived(
        restProps["aria-label"] || restProps["aria-labelledby"] || title,
    );

    let attributes = $derived({
        "aria-hidden": labelled ? undefined : true,
        role: labelled ? "img" : undefined,
        focusable: Number(restProps["tabindex"]) === 0 ? "true" : undefined,
    });
</script>

<svg
    xmlns="http://www.w3.org/2000/svg"
    width={size}
    height={size}
    viewBox="0 0 24 24"
    {...restProps}
    {...attributes}>
    {#if title}
        <title>{title}</title>
    {/if}

    <path d="M0 0h24v24H0z" fill="none" />
</svg>
