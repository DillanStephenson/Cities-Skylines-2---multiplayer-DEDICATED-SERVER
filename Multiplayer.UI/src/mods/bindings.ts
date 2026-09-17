import { bindLocalValue, bindValue, trigger } from "cs2/api";

// Must match MultiplayerUISystem.Group and its binding names on the C# side.
const GROUP = "multiplayer";

export const screenActive$ = bindValue<boolean>(GROUP, "screenActive", false);

export const playerName$ = bindValue<string>(GROUP, "playerName", "");
export const hostPort$ = bindValue<string>(GROUP, "hostPort", "");
export const hostPassword$ = bindValue<string>(GROUP, "hostPassword", "");
export const joinAddress$ = bindValue<string>(GROUP, "joinAddress", "");
export const joinPassword$ = bindValue<string>(GROUP, "joinPassword", "");
export const joinOwnerKey$ = bindValue<string>(GROUP, "joinOwnerKey", "");

export const state$ = bindValue<string>(GROUP, "state", "Offline");
export const online$ = bindValue<boolean>(GROUP, "online", false);
export const isOwner$ = bindValue<boolean>(GROUP, "isOwner", false);
export const inGame$ = bindValue<boolean>(GROUP, "inGame", false);
export const serverName$ = bindValue<string>(GROUP, "serverName", "");
export const players$ = bindValue<string>(GROUP, "players", "");
export const world$ = bindValue<string>(GROUP, "world", "");
export const worldStatus$ = bindValue<string>(GROUP, "worldStatus", "");
export const lastError$ = bindValue<string>(GROUP, "lastError", "");
export const recent$ = bindValue<string>(GROUP, "recent", "");
export const newerCity$ = bindValue<boolean>(GROUP, "newerCity", false);

/** UI-only: whether the in-game panel is open. Shared between the toolbar button and the panel. */
export const panelOpen$ = bindLocalValue<boolean>(false);
export const togglePanel = () => panelOpen$.update(!panelOpen$.value);

export const setPlayerName = (value: string) => trigger(GROUP, "setPlayerName", value);
export const setHostPort = (value: string) => trigger(GROUP, "setHostPort", value);
export const setHostPassword = (value: string) => trigger(GROUP, "setHostPassword", value);
export const setJoinAddress = (value: string) => trigger(GROUP, "setJoinAddress", value);
export const setJoinPassword = (value: string) => trigger(GROUP, "setJoinPassword", value);
export const setJoinOwnerKey = (value: string) => trigger(GROUP, "setJoinOwnerKey", value);

export const host = () => trigger(GROUP, "host");
export const join = () => trigger(GROUP, "join");
export const leave = () => trigger(GROUP, "leave");
export const uploadCity = () => trigger(GROUP, "uploadCity");
export const fetchCity = () => trigger(GROUP, "fetchCity");
export const openScreen = () => trigger(GROUP, "openScreen");
export const screenExited = () => trigger(GROUP, "screenExited");
