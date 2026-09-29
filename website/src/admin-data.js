import { db, sessionUserId } from './cloudbase.js';

const CAMPUS_COLUMNS = 'id,name,region,city,status,created_at,updated_at';
const TELEMETRY_COLUMNS = 'day_utc,unique_devices,heartbeat_signals,updated_at';

function requireDatabase() {
  if (!db) throw new Error('CloudBase 数据库客户端没有初始化。');
  return db;
}

export async function loadAdminProfile(session) {
  const userId = sessionUserId(session);
  if (!userId) return null;
  const client = requireDatabase();
  const { data, error } = await client
    .from('admin_profiles')
    .select('user_id,display_name,role')
    .eq('user_id', userId)
    .limit(1);
  if (error) throw error;
  return Array.isArray(data) ? data[0] ?? null : null;
}

export async function loadAdminData(days = 30) {
  const client = requireDatabase();
  const end = new Date();
  const start = new Date(end.getTime() - (days - 1) * 86_400_000);
  const fromDay = start.toISOString().slice(0, 10);
  const throughDay = end.toISOString().slice(0, 10);
  const [campusesResult, activeCountResult, statsResult] = await Promise.all([
    client.from('campuses')
      .select(CAMPUS_COLUMNS, { count: 'exact' })
      .order('updated_at', { ascending: false })
      .limit(200),
    client.from('campuses')
      .select('id', { count: 'exact' })
      .eq('status', 'active')
      .limit(1),
    client.from('telemetry_daily_stats')
      .select(TELEMETRY_COLUMNS)
      .gte('day_utc', fromDay)
      .lte('day_utc', throughDay)
      .order('day_utc', { ascending: true })
      .limit(days)
  ]);
  if (campusesResult.error) throw campusesResult.error;
  if (activeCountResult.error) throw activeCountResult.error;
  if (statsResult.error) throw statsResult.error;
  return {
    campuses: Array.isArray(campusesResult.data) ? campusesResult.data : [],
    campusCount: Number(campusesResult.count ?? campusesResult.data?.length ?? 0),
    activeCampusCount: Number(activeCountResult.count ?? activeCountResult.data?.length ?? 0),
    telemetry: Array.isArray(statsResult.data) ? statsResult.data : []
  };
}

export async function createCampus(fields) {
  const client = requireDatabase();
  const { data, error } = await client
    .from('campuses')
    .insert({
      name: fields.name.trim(),
      region: fields.region.trim(),
      city: fields.city.trim(),
      status: fields.status || 'active'
    })
    .select(CAMPUS_COLUMNS);
  if (error) throw error;
  return Array.isArray(data) ? data[0] ?? null : data;
}

export async function updateCampus(id, fields) {
  const client = requireDatabase();
  const { data, error } = await client
    .from('campuses')
    .update({
      name: fields.name.trim(),
      region: fields.region.trim(),
      city: fields.city.trim(),
      status: fields.status
    })
    .eq('id', id)
    .select(CAMPUS_COLUMNS);
  if (error) throw error;
  return Array.isArray(data) ? data[0] ?? null : data;
}
