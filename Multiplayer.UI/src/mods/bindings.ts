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
/** True while the host, just connected from the menu, has to say: load the server's city, or start a new world. */
export const hostChoice$ = bindValue<boolean>(GROUP, "hostChoice", false);
/** JSON list of the other players' name tags with screen positions, refreshed every other frame while in a city. */
export const presence$ = bindValue<string>(GROUP, "presence", "");
/** True while the city is being saved and uploaded, or downloaded and loaded. */
export const transferBusy$ = bindValue<boolean>(GROUP, "transferBusy", false);
/** "join", "host", "choice" or "panel" when the C# side wants a screen opened without a click (dev trigger). */
export const requestedView$ = bindValue<string>(GROUP, "requestedView", "");

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
/** Connect with the owner key and open New Game; the city started next replaces the one on the server. */
export const joinNewCity = () => trigger(GROUP, "joinNewCity");
export const chooseLoad = () => trigger(GROUP, "chooseLoad");
export const chooseNewWorld = () => trigger(GROUP, "chooseNewWorld");
export const leave = () => trigger(GROUP, "leave");
export const uploadCity = () => trigger(GROUP, "uploadCity");
export const fetchCity = () => trigger(GROUP, "fetchCity");
export const openScreen = () => trigger(GROUP, "openScreen");
export const screenExited = () => trigger(GROUP, "screenExited");
