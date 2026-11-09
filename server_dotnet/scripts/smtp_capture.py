"""Loopback-only TLS SMTP capture for disposable browser acceptance; never relays mail."""
import base64
import contextlib
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import secrets
import socketserver
import ssl
import tempfile
import threading


class SmtpCapture:
    def __init__(self, pfx_path, password, temporary_root):
        from cryptography.hazmat.primitives.serialization import Encoding, PrivateFormat, NoEncryption, pkcs12
        key, certificate, _ = pkcs12.load_key_and_certificates(Path(pfx_path).read_bytes(), password.encode())
        self.temporary = tempfile.TemporaryDirectory(prefix='smtp_capture_', dir=temporary_root)
        directory = Path(self.temporary.name)
        self.cert_path, self.key_path = directory / 'certificate.pem', directory / 'key.private.pem'
        self.cert_path.write_bytes(certificate.public_bytes(Encoding.PEM))
        self.key_path.write_bytes(key.private_bytes(Encoding.PEM, PrivateFormat.PKCS8, NoEncryption()))
        self.tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        self.tls.load_cert_chain(self.cert_path, self.key_path)
        self.username = 'capture@example.invalid'
        self.password = 'Capture#' + secrets.token_urlsafe(18)
        self.token = secrets.token_urlsafe(32)
        self.lock = threading.Lock()
        self.messages = []
        self.attempts = 0
        self.mode = 'accept'
        self.failures_left = 0
        owner = self

        class Smtp(socketserver.StreamRequestHandler):
            def reply(self, line):
                self.wfile.write((line + '\r\n').encode()); self.wfile.flush()

            def handle(self):
                self.connection.settimeout(60)
                tls, authenticated = False, False
                sender, recipients = '', []
                try:
                    self.reply('220 localhost YF disposable SMTP capture')
                    while True:
                        raw = self.rfile.readline(1024 * 1024)
                        if not raw: return
                        line = raw.decode(errors='replace').rstrip('\r\n')
                        command, _, value = line.partition(' ')
                        command = command.upper()
                        if command in ('EHLO', 'HELO'):
                            self.reply('250-localhost\r\n250-STARTTLS\r\n250 AUTH PLAIN LOGIN' if not tls else '250-localhost\r\n250 AUTH PLAIN LOGIN')
                        elif command == 'STARTTLS' and not tls:
                            self.reply('220 Ready to start TLS')
                            self.rfile.close(); self.wfile.close()
                            self.connection = owner.tls.wrap_socket(self.connection, server_side=True)
                            self.rfile = self.connection.makefile('rb'); self.wfile = self.connection.makefile('wb')
                            tls = True
                        elif command == 'AUTH':
                            if not tls: self.reply('530 STARTTLS required'); continue
                            mechanism, _, credentials = value.partition(' ')
                            if mechanism.upper() == 'PLAIN':
                                if not credentials: self.reply('334 '); credentials = self.rfile.readline().strip().decode()
                                parts = base64.b64decode(credentials).decode().split('\0')
                                username, password_value = parts[-2:]
                            elif mechanism.upper() == 'LOGIN':
                                if not credentials: self.reply('334 VXNlcm5hbWU6'); credentials = self.rfile.readline().strip().decode()
                                username = base64.b64decode(credentials).decode()
                                self.reply('334 UGFzc3dvcmQ6')
                                password_value = base64.b64decode(self.rfile.readline().strip()).decode()
                            else: self.reply('504 Unsupported mechanism'); continue
                            authenticated = username == owner.username and password_value == owner.password and owner.mode != 'auth_failure'
                            self.reply('235 Authentication successful' if authenticated else '535 Authentication failed')
                        elif command == 'MAIL':
                            if not authenticated: self.reply('530 Authentication required'); continue
                            sender = value; recipients = []; self.reply('250 Sender accepted')
                        elif command == 'RCPT':
                            if not authenticated: self.reply('530 Authentication required'); continue
                            if owner.mode == 'reject_recipient': self.reply('550 Mailbox unavailable'); continue
                            recipients.append(value); self.reply('250 Recipient accepted')
                        elif command == 'DATA':
                            if not authenticated or not recipients: self.reply('503 Recipient required'); continue
                            self.reply('354 End data with <CRLF>.<CRLF>')
                            lines = []
                            while True:
                                data = self.rfile.readline(1024 * 1024)
                                if not data: return
                                if data == b'.\r\n': break
                                lines.append(data[1:] if data.startswith(b'..') else data)
                            with owner.lock:
                                owner.attempts += 1
                                reject = owner.mode == 'transient' and owner.failures_left > 0
                                if reject: owner.failures_left -= 1
                                else: owner.messages.append({'sender': sender, 'recipients': recipients[:], 'body': b''.join(lines).decode(errors='replace')})
                            if reject: self.reply('451 Temporary fixture failure')
                            elif owner.mode == 'accept_drop': return  # accepted payload but no acknowledgment: outcome is unknown to client
                            else: self.reply('250 Accepted by local capture')
                        elif command == 'QUIT': self.reply('221 Bye'); return
                        elif command in ('RSET', 'NOOP'): self.reply('250 OK')
                        else: self.reply('502 Unsupported command')
                except (OSError, ValueError, ssl.SSLError):
                    return

        class Control(BaseHTTPRequestHandler):
            def do_GET(self):
                if self.headers.get('X-Test-Key') != owner.token: self.send_error(403); return
                with owner.lock: data = {'messages': owner.messages[:], 'attempts': owner.attempts, 'mode': owner.mode}
                self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers(); self.wfile.write(json.dumps(data).encode())
            def do_POST(self):
                if self.headers.get('X-Test-Key') != owner.token: self.send_error(403); return
                data = json.loads(self.rfile.read(int(self.headers.get('Content-Length', '0'))))
                if data.get('mode') not in ('accept', 'auth_failure', 'reject_recipient', 'transient', 'accept_drop'): self.send_error(400); return
                with owner.lock:
                    owner.mode = data['mode']; owner.failures_left = int(data.get('failures', 1))
                self.send_response(200); self.end_headers(); self.wfile.write(b'{}')
            def log_message(self, *_): pass

        class Server(socketserver.ThreadingTCPServer):
            allow_reuse_address = True
            daemon_threads = True
        self.smtp = Server(('127.0.0.1', 0), Smtp)
        self.control = ThreadingHTTPServer(('127.0.0.1', 0), Control)

    def __enter__(self):
        for server in (self.smtp, self.control): threading.Thread(target=server.serve_forever, daemon=True).start()
        return self

    @property
    def browser_settings(self):
        return {'host': 'localhost', 'port': self.smtp.server_address[1], 'username': self.username,
                'password': self.password, 'from': self.username, 'security': 'StartTls',
                'control': f'http://127.0.0.1:{self.control.server_address[1]}', 'token': self.token}

    def __exit__(self, *_):
        for server in (self.smtp, self.control): server.shutdown(); server.server_close()
        self.temporary.cleanup()
