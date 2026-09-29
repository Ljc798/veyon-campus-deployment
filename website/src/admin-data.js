import { db, sessionUserId } from './cloudbase.js';

const CAMPUS_COLUMNS = 'id,name,region,city,status,created_at,updated_at';
const TELEMETRY_COLUMNS = 'day_hkt,unique_devices,heartbeat_signals,updated_at';
const DEPLOYMENT_TELEMETRY_COLUMNS = 'day_hkt,campus_id,deployment_id,application_version,unique_devices,heartbeat_signals,updated_at';

function hongKongDay(date) {
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone: 'Asia/Hong_Kong', year: 'numeric', month: '2-digit', day: '2-digit'
  }).formatToParts(date);
  const part = type => parts.find(item => item.type === type)?.value;
  return `${part('year')}-${part('month')}-${part('day')}`;
}

function shiftDay(day, amount) {
  const [year, month, date] = day.split('-').map(Number);
  return new Date(Date.UTC(year, month - 1, date + amount)).toISOString().slice(0, 10);
}

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
  const throughDay = hongKongDay(new Date());
  const fromDay = shiftDay(throughDay, -(days - 1));
  const [campusesResult, activeCountResult, statsResult, deploymentStatsResult] = await Promise.all([
    client.from('campuses')
      .select(CAMPUS_COLUMNS, { count: 'exact' })
      .order('updated_at', { ascending: false })
      .limit(200),
    client.from('campuses')
      .select('id', { count: 'exact' })
      .eq('status', 'active')
      .limit(1),
    client.from('telemetry_daily_hkt_stats')
      .select(TELEMETRY_COLUMNS)
      .gte('day_hkt', fromDay)
      .lte('day_hkt', throughDay)
      .order('day_hkt', { ascending: true })
      .limit(days),
    client.from('telemetry_daily_deployment_stats')
      .select(DEPLOYMENT_TELEMETRY_COLUMNS)
      .gte('day_hkt', fromDay)
      .lte('day_hkt', throughDay)
      .order('day_hkt', { ascending: false })
      .limit(1000)
  ]);
  if (campusesResult.error) throw campusesResult.error;
  if (activeCountResult.error) throw activeCountResult.error;
  if (statsResult.error) throw statsResult.error;
  if (deploymentStatsResult.error) throw deploymentStatsResult.error;
  return {
    campuses: Array.isArray(campusesResult.data) ? campusesResult.data : [],
    campusCount: Number(campusesResult.count ?? campusesResult.data?.length ?? 0),
    activeCampusCount: Number(activeCountResult.count ?? activeCountResult.data?.length ?? 0),
    telemetry: Array.isArray(statsResult.data) ? statsResult.data : [],
    deploymentTelemetry: Array.isArray(deploymentStatsResult.data) ? deploymentStatsResult.data : []
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
