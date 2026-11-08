import unittest
from email.message import EmailMessage
from email import policy

from smtp_capture import parse_mime_message


class SmtpCaptureMimeTests(unittest.TestCase):
    def test_decodes_unicode_subject_plain_text_and_html(self):
        message = EmailMessage(policy=policy.SMTP)
        message['From'] = 'sender@example.invalid'
        message['To'] = 'recipient@example.invalid'
        message['Subject'] = '[ODM/OEMA 文件传递] 新文件已送达 · 传递单 #123'
        message.set_content('传递单编号：#123\n请登录：https://example.invalid/oem-portal/transfers/123')
        message.add_alternative(
            '<html><body><strong>传递单编号：#123</strong>'
            '<a href="https://example.invalid/oem-portal/transfers/123">查看</a></body></html>',
            subtype='html')

        parsed = parse_mime_message(message.as_bytes())

        self.assertEqual('[ODM/OEMA 文件传递] 新文件已送达 · 传递单 #123', parsed['subject'])
        self.assertIn('传递单编号：#123', parsed['text'])
        self.assertIn('/oem-portal/transfers/123', parsed['text'])
        self.assertIn('传递单编号：#123', parsed['html'])
        self.assertIn('href="https://example.invalid/oem-portal/transfers/123"', parsed['html'])


if __name__ == '__main__':
    unittest.main()
