'use strict';

// Keep CloudBase CLI DNS lookups on domestic recursive resolvers without
// changing the machine-wide DNS configuration.
const dns = require('node:dns');
const net = require('node:net');

const resolver = new dns.Resolver();
resolver.setServers(['119.29.29.29', '223.5.5.5']);

const systemLookup = dns.lookup.bind(dns);

function resolveRecords(hostname, family, callback) {
  const records = [];
  let pending = 0;
  let firstError = null;

  const finish = (error, addresses) => {
    if (error && !firstError) firstError = error;
    if (addresses?.length) records.push(...addresses);
    pending--;
    if (pending !== 0) return;
    if (records.length) callback(null, records);
    else callback(firstError || new Error('ENOTFOUND'));
  };

  if (family !== 6) {
    pending++;
    resolver.resolve4(hostname, (error, addresses) =>
      finish(error, (addresses || []).map(address => ({ address, family: 4 }))));
  }
  if (family !== 4) {
    pending++;
    resolver.resolve6(hostname, (error, addresses) =>
      finish(error, (addresses || []).map(address => ({ address, family: 6 }))));
  }
}

function domesticLookup(hostname, options, callback) {
  if (typeof options === 'function') {
    callback = options;
    options = {};
  } else if (typeof options === 'number') {
    options = { family: options };
  }

  options ||= {};
  if (typeof callback !== 'function' || typeof hostname !== 'string' || !hostname ||
      hostname === 'localhost' || hostname.endsWith('.local') || net.isIP(hostname)) {
    return systemLookup(hostname, options, callback);
  }

  resolveRecords(hostname, Number(options.family || 0), (error, records) => {
    if (error) return callback(error);
    if (options.all) return callback(null, records);
    const record = records[0];
    callback(null, record.address, record.family);
  });
}

dns.lookup = domesticLookup;
dns.promises.lookup = (hostname, options) => new Promise((resolve, reject) => {
  domesticLookup(hostname, options, (error, address, family) => {
    if (error) return reject(error);
    resolve(options?.all ? address : { address, family });
  });
});
