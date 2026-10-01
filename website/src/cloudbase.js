import cloudbase from '@cloudbase/js-sdk';

const env = import.meta.env.VITE_CLOUDBASE_ENV_ID;
const accessKey = import.meta.env.VITE_CLOUDBASE_PUBLISHABLE_KEY;
const region = import.meta.env.VITE_CLOUDBASE_REGION || 'ap-shanghai';

export const cloudbaseReady = Boolean(env && accessKey);
export const cloudbaseApp = cloudbaseReady
  ? cloudbase.init({ env, accessKey, region })
  : null;
export const auth = cloudbaseApp?.auth ?? null;
export const db = cloudbaseApp?.rdb() ?? null;

export async function getActiveSession() {
  if (!auth) return null;
  const { data, error } = await auth.getSession();
  if (error) throw error;
  const session = data?.session;
  if (!session || session.user?.is_anonymous) return null;
  return session;
}

export async function signIn(username, password) {
  if (!auth) throw new Error('CloudBase 前端配置尚未完成。');
  const { data, error } = await auth.signInWithPassword({ username, password });
  if (error) throw error;
  if (!data?.session) throw new Error('登录未建立有效会话，请检查账号配置。');
  return data.session;
}

export async function signOut() {
  if (!auth) return;
  const { error } = await auth.signOut();
  if (error) throw error;
}

export function subscribeToAuthChanges(listener) {
  if (!auth) return () => {};
  const { data, error } = auth.onAuthStateChange(listener);
  if (error) throw error;
  return () => data?.subscription?.unsubscribe?.();
}

export function sessionUserId(session) {
  const user = session?.user;
  const id = user?.id || user?.sub || user?.uid;
  return typeof id === 'string' && id.length > 0 ? id : null;
}
