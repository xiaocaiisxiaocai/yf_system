'use strict';

// Long-lived SignalR probe used by test_signalr_contracts.py. Commands, including
// bearer tokens, arrive as JSON lines on stdin. Stdout is reserved for sanitized
// request/result envelopes and never echoes command arguments.

const path = require('node:path');
const readline = require('node:readline');

const signalRRoot = path.resolve(__dirname, '../../web/node_modules/@microsoft/signalr');
const signalRPackage = require(path.join(signalRRoot, 'package.json'));
if (signalRPackage.version !== '10.0.11') {
  process.stderr.write('SignalR client 10.0.11 is required\n');
  process.exit(2);
}
const {
  HubConnectionBuilder,
  HttpTransportType,
  LogLevel,
} = require(signalRRoot);

const clients = new Map();

function emit(id, result) {
  process.stdout.write(`${JSON.stringify({ id, ok: true, result })}\n`);
}

function emitFailure(id) {
  process.stdout.write(`${JSON.stringify({ id, ok: false, error: 'command_failed' })}\n`);
}

function delay(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

function findEvent(client, command) {
  const from = Number.isInteger(command.since) ? command.since : 0;
  return client.events.slice(from).find((entry) => {
    if (command.projectId !== undefined && entry.payload?.projectId !== command.projectId) return false;
    if (command.kind !== undefined && entry.payload?.kind !== command.kind) return false;
    return true;
  });
}

async function connect(command) {
  if (clients.has(command.name)) throw new Error('duplicate client');
  const options = {
    headers: { Origin: command.origin || command.baseUrl },
  };
  if (typeof command.accessToken === 'string' && command.accessToken.length > 0) {
    options.accessTokenFactory = () => command.accessToken;
  }
  if (command.webSocketsOnly) {
    options.transport = HttpTransportType.WebSockets;
    options.skipNegotiation = true;
  }

  const connection = new HubConnectionBuilder()
    .withUrl(`${command.baseUrl}/api/v1/collaboration/live`, options)
    .configureLogging(LogLevel.None)
    .build();
  connection.serverTimeoutInMilliseconds = 30000;
  connection.handshakeTimeoutInMilliseconds = 3000;
  const client = { connection, events: [], closedAt: null };
  connection.on('ProjectChanged', (payload) => {
    client.events.push({ payload, receivedAt: Date.now() });
  });
  connection.onclose(() => {
    client.closedAt = Date.now();
  });

  try {
    await connection.start();
  } catch {
    try { await connection.stop(); } catch { /* already stopped */ }
    return { connected: false };
  }
  clients.set(command.name, client);
  return { connected: true };
}

async function handle(command) {
  switch (command.action) {
    case 'connect':
      return connect(command);
    case 'mark': {
      const client = clients.get(command.name);
      if (!client) throw new Error('unknown client');
      return { index: client.events.length };
    }
    case 'wait': {
      const client = clients.get(command.name);
      if (!client) throw new Error('unknown client');
      const deadline = Date.now() + Math.max(0, command.timeoutMs || 0);
      let event;
      while (!(event = findEvent(client, command)) && Date.now() < deadline) await delay(10);
      return { event: event || null, eventCount: client.events.length };
    }
    case 'events': {
      const client = clients.get(command.name);
      if (!client) throw new Error('unknown client');
      const from = Number.isInteger(command.since) ? command.since : 0;
      return { events: client.events.slice(from) };
    }
    case 'waitClosed': {
      const client = clients.get(command.name);
      if (!client) throw new Error('unknown client');
      const deadline = Date.now() + Math.max(0, command.timeoutMs || 0);
      while (client.closedAt === null && Date.now() < deadline) await delay(10);
      return { closed: client.closedAt !== null };
    }
    case 'stop': {
      const client = clients.get(command.name);
      if (client) {
        clients.delete(command.name);
        await client.connection.stop();
      }
      return { stopped: true };
    }
    case 'shutdown':
      await Promise.allSettled([...clients.values()].map((client) => client.connection.stop()));
      clients.clear();
      return { stopped: true };
    default:
      throw new Error('unknown action');
  }
}

const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
let chain = Promise.resolve();
input.on('line', (line) => {
  chain = chain.then(async () => {
    let command;
    try {
      command = JSON.parse(line);
      const result = await handle(command);
      emit(command.id, result);
      if (command.action === 'shutdown') process.exitCode = 0;
    } catch {
      emitFailure(command?.id ?? null);
    }
  });
});
input.on('close', () => {
  chain.finally(async () => {
    await Promise.allSettled([...clients.values()].map((client) => client.connection.stop()));
  });
});
