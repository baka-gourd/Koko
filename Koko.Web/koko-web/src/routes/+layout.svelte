<script lang="ts">
    // import "./layout.css";
    import "carbon-components-svelte/css/all.css";
    import favicon from "$lib/assets/favicon.svg";
    import { page } from "$app/state";

    let { children } = $props();

    import {
        Content,
        Header,
        HeaderUtilities,
        HeaderAction,
        HeaderPanelLinks,
        HeaderPanelDivider,
        HeaderPanelLink,
        SideNav,
        SideNavDivider,
        SideNavItems,
        SideNavLink,
        SideNavMenu,
        SideNavMenuItem,
        SkipToContent,
        HeaderGlobalAction,
        NotificationQueue,
        Theme,
    } from "carbon-components-svelte";

    import Fade from "carbon-icons-svelte/lib/Fade.svelte";
    import Switcher from "carbon-icons-svelte/lib/Switcher.svelte";
    import Light from "carbon-icons-svelte/lib/Light.svelte";
    import Moon from "carbon-icons-svelte/lib/Moon.svelte";
    import BlockStorageAlt from "carbon-icons-svelte/lib/BlockStorageAlt.svelte";
    import Home from "carbon-icons-svelte/lib/Home.svelte";
    import Settings from "carbon-icons-svelte/lib/Settings.svelte";
    import Placeholder from "$lib/components/icons/Placeholder.svelte";
    import { bindNotificationQueue } from "$lib/notifications";
    import type { CarbonTheme } from "carbon-components-svelte/src/Theme/Theme.svelte";
    import type NotificationQueueComponent from "carbon-components-svelte/src/Notification/NotificationQueue.svelte";

    let isSideNavOpen = $state(false);
    let isAppSwitcherOpen = $state(false);
    let notificationQueue = $state<NotificationQueueComponent>();
    let theme: CarbonTheme = $state("g100");
    let isDark = $derived(theme === "g100");
    const sideNavHrefs = [
        "/",
        "/metadata",
        "/ltfs",
        "/settings/fileShortcut",
        "/settings/fileGlobbing",
        "/files/management",
        "/settings",
    ] as const;

    function isSideNavHrefMatch(pathname: string, href: string) {
        return href === "/"
            ? pathname === "/"
            : pathname === href || pathname.startsWith(`${href}/`);
    }

    function getSelectedSideNavHref(pathname: string) {
        return sideNavHrefs.reduce<string | undefined>((selectedHref, href) => {
            if (!isSideNavHrefMatch(pathname, href)) return selectedHref;
            if (!selectedHref || href.length > selectedHref.length) return href;
            return selectedHref;
        }, undefined);
    }

    let selectedSideNavHref = $derived(
        getSelectedSideNavHref(page.url.pathname),
    );

    function toggleTheme() {
        if (isDark) {
            theme = "white";
        } else {
            theme = "g100";
        }
    }

    $effect(() => {
        bindNotificationQueue(notificationQueue);
    });
</script>

<svelte:head><link rel="icon" href={favicon} /></svelte:head>
<Theme bind:theme persist persistKey="__carbon-theme" />
<Header companyName="Koko" platformName="Tape Management" bind:isSideNavOpen>
    <svelte:fragment slot="skipToContent">
        <SkipToContent />
    </svelte:fragment>

    <HeaderUtilities>
        <HeaderGlobalAction
            icon={isDark ? Moon : Light}
            on:click={toggleTheme}
            iconDescription="切换主题"
            tooltipAlignment="start" />
        <HeaderAction
            aria-label="App switcher"
            icon={Switcher}
            bind:isOpen={isAppSwitcherOpen}>
            <HeaderPanelLinks>
                <HeaderPanelDivider>Applications</HeaderPanelDivider>

                <HeaderPanelLink href="/app/dashboard">
                    Dashboard
                </HeaderPanelLink>

                <HeaderPanelLink href="/app/files">Files</HeaderPanelLink>

                <HeaderPanelLink href="/app/tasks">Tasks</HeaderPanelLink>

                <HeaderPanelDivider>Administration</HeaderPanelDivider>

                <HeaderPanelLink href="/settings">Settings</HeaderPanelLink>

                <HeaderPanelLink href="/logs">Logs</HeaderPanelLink>
            </HeaderPanelLinks>
        </HeaderAction>
    </HeaderUtilities>
</Header>

<SideNav bind:isOpen={isSideNavOpen} ariaLabel="Side navigation">
    <SideNavItems>
        <SideNavLink
            icon={Home}
            text="主页"
            href="/"
            isSelected={selectedSideNavHref === "/"} />

        <SideNavLink
            icon={Placeholder}
            text="元数据"
            href="/metadata"
            isSelected={selectedSideNavHref === "/metadata"} />

        <SideNavLink
            icon={BlockStorageAlt}
            text="浏览"
            href="/ltfs"
            isSelected={selectedSideNavHref === "/ltfs"} />

        <SideNavMenu icon={Placeholder} text="本机文件管理">
            <SideNavMenuItem
                href="/settings/fileShortcut"
                text="快捷访问"
                isSelected={selectedSideNavHref === "/settings/fileShortcut"} />
            <SideNavMenuItem
                href="/settings/fileGlobbing"
                text="文件排除"
                isSelected={selectedSideNavHref === "/settings/fileGlobbing"} />
            <SideNavMenuItem
                href="/files/management"
                text="临时目录管理"
                isSelected={selectedSideNavHref === "/files/management"} />
        </SideNavMenu>

        <SideNavDivider />

        <SideNavLink
            icon={Settings}
            text="设置"
            href="/settings"
            isSelected={selectedSideNavHref === "/settings"} />
    </SideNavItems>
</SideNav>

<Content>
    {@render children?.()}
</Content>

<NotificationQueue
    bind:this={notificationQueue}
    position="top-right"
    offsetTop="4rem"
    maxNotifications={5} />
