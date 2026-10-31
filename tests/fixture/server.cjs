// Loopback-only SSH/HTTP/WebSocket fixture. No shell execution, no remote credentials.
const { Server } = require('ssh2');
const { WebSocketServer } = require('ws');
const crypto = require('node:crypto');
const net = require('node:net');
const http = require('node:http');
const fs = require('node:fs');
const { privateKey, publicKey } = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
const pem = privateKey.export({ type:'pkcs1', format:'pem' });
const pair = crypto.generateKeyPairSync('rsa', { modulusLength:2048 });
fs.writeFileSync(process.argv[2], pair.privateKey.export({type:'pkcs1',format:'pem',cipher:'aes-256-cbc',passphrase:'fixture-passphrase'}));
const expectedKey = require('ssh2').utils.parseKey(pair.privateKey.export({type:'pkcs1',format:'pem'})).getPublicSSH();
const clients = new Set(); const bridges = new Set(); let blackhole = false;
let forwards = 0, targetRequests = 0;
const ssh = new Server({hostKeys:[pem]}, c => {
 clients.add(c); c.on('error',()=>{}); c.on('close',()=>clients.delete(c));
 c.on('authentication',ctx=>{
  if(ctx.username !== 'fixture') return ctx.reject(['password','publickey']);
  if(ctx.method==='password' && ctx.password==='fixture-password') return ctx.accept();
  if(ctx.method==='publickey' && ctx.key.data.equals(expectedKey)) {
   if(!ctx.signature || crypto.verify(ctx.hashAlgo, ctx.blob, pair.publicKey, ctx.signature)) return ctx.accept();
  }
  ctx.reject(['password','publickey']);
 });
 c.on('tcpip',(accept,reject,info)=>{
  forwards++;
  if(info.destIP !== '127.0.0.1' || info.destPort !== 18189) return reject();
  const socket = net.connect(18189,'127.0.0.1');
  socket.once('connect',()=>{const stream=accept();stream.on('error',()=>socket.destroy());socket.on('error',()=>stream.destroy());stream.pipe(socket).pipe(stream);stream.on('close',()=>socket.destroy());socket.on('close',()=>stream.destroy());});
  socket.once('error',()=>reject());
 });
});
const target = http.createServer((req,res)=>{
 targetRequests++;
 if(req.url==='/large') {res.writeHead(200,{'Content-Length':8*1024*1024}); return res.end(Buffer.alloc(8*1024*1024,0x5a));}
 if(req.url==='/upload') {let bytes=0;req.on('data',b=>bytes+=b.length);req.on('end',()=>res.end(String(bytes)));return;}
 res.end('fixture target');
});
const ws = new WebSocketServer({server:target});ws.on('connection',s=>s.on('message',d=>s.send(d)));
function drop(){for(const [a,b] of bridges){a.destroy();b.destroy();}for(const c of clients)c.end();}
const control=http.createServer((req,res)=>{
 if(req.url==='/stats'){res.setHeader('Content-Type','application/json');return res.end(JSON.stringify({forwards,targetRequests}));}
 if(req.url==='/drop') drop();
 if(req.url==='/blackhole') blackhole=true;
 if(req.url==='/restore') blackhole=false;
 if(req.url==='/target-off'){target.close();target.closeAllConnections();}
 if(req.url==='/target-on' && !target.listening) target.listen(18189,'127.0.0.1');
 res.end('ok');
});
ssh.listen(0,'127.0.0.1',()=>{
 const proxy=net.createServer(a=>{
  const b=net.connect(ssh.address().port,'127.0.0.1');const pair=[a,b];bridges.add(pair);
  a.on('data',d=>{if(!blackhole)b.write(d);});b.on('data',d=>{if(!blackhole)a.write(d);});
  for(const s of pair){s.on('error',()=>{});s.on('close',()=>{a.destroy();b.destroy();bridges.delete(pair);});}
 });
 proxy.listen(22222,'127.0.0.1',()=>target.listen(18189,'127.0.0.1',()=>control.listen(18190,'127.0.0.1',()=>console.log('READY'))));
});
