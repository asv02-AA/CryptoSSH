// Development-only, loopback SSH fixture. Not included in the Windows application.
const { Server, utils } = require('../tests/ssh-fixture/node_modules/ssh2');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../artifacts/ssh-integration');
fs.mkdirSync(root, { recursive: true });
const hostKey = utils.generateKeyPairSync('rsa', { bits: 2048 });
const testKey = utils.generateKeyPairSync('ed25519');
fs.writeFileSync(path.join(root, 'id_ed25519'), testKey.private);
if (process.platform === 'win32') {
  const keyFile = path.join(root, 'id_ed25519');
  require('node:child_process').execFileSync('icacls.exe', [keyFile, '/inheritance:r', '/grant:r', `${process.env.USERDOMAIN}\\${process.env.USERNAME}:(F)`], { windowsHide: true });
}
const allowed = utils.parseKey(testKey.public).getPublicSSH();
const tokenKey = process.env.CRYPTOSSH_TEST_TOKEN_PUBLIC ? utils.parseKey(fs.readFileSync(process.env.CRYPTOSSH_TEST_TOKEN_PUBLIC)) : null;
const server = new Server({ hostKeys: [hostKey.private] }, client => {
  client.on('error', () => {});
  client.on('authentication', ctx => {
    if (ctx.username !== 'fixture') return ctx.reject();
    if (ctx.method === 'password' && ctx.password === 'test-fixture-only') return ctx.accept();
    if (ctx.method === 'publickey' && ctx.key.data.equals(allowed)) {
      if (!ctx.signature || utils.parseKey(testKey.public).verify(ctx.blob, ctx.signature, ctx.hashAlgo) === true) return ctx.accept();
    }
    if (ctx.method === 'publickey' && tokenKey && ctx.key.data.equals(tokenKey.getPublicSSH())) {
      if (!ctx.signature || tokenKey.verify(ctx.blob, ctx.signature, ctx.hashAlgo) === true) return ctx.accept();
    }
    ctx.reject(['password', 'publickey']);
  });
  client.on('ready', () => client.on('session', accept => {
    const session = accept();
    session.on('pty', accept => accept());
    session.on('window-change', accept => accept?.());
    session.on('shell', accept => {
      const stream = accept(); stream.write('\x1b[32mSSH_INTEGRATION_OK\x1b[0m\r\nfixture> ');
      let input = '';
      stream.on('data', data => {
        for (const ch of data.toString()) {
          if (ch === '\r' || ch === '\n') {
            stream.write('\r\n');
            if (input === 'exit') { stream.exit(0); stream.end(); return; }
            if (input === 'probe') stream.write('PROBE_EXECUTED\r\n');
            input = ''; stream.write('fixture> ');
          } else if (ch === '\x7f') { input = input.slice(0, -1); stream.write('\b \b'); }
          else { input += ch; stream.write(ch); }
        }
      });
    });
  }));
});
server.listen(0, '127.0.0.1', () => { fs.writeFileSync(path.join(root, 'port.txt'), String(server.address().port)); console.log(`Loopback SSH fixture: ${server.address().port}`); });
setTimeout(() => server.close(), 180000).unref();
