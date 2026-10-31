import os
import unittest
from unittest.mock import MagicMock, patch

import psycopg
from app.main import health, ready


class HealthTests(unittest.TestCase):
    def setUp(self):
        environment = patch.dict(os.environ, {"POSTGRES_HOST": "postgres"})
        environment.start()
        self.addCleanup(environment.stop)

    def test_liveness_does_not_require_database(self):
        with patch("app.main.psycopg.connect") as connect:
            response = health()
        self.assertEqual(response["status"], "Healthy")
        connect.assert_not_called()

    def test_standalone_readiness_does_not_require_database(self):
        with patch.dict(os.environ, {"POSTGRES_HOST": ""}), patch("app.main.psycopg.connect") as connect:
            response = ready()
        self.assertEqual(response["status"], "Healthy")
        connect.assert_not_called()

    def test_readiness_executes_database_query(self):
        connection = MagicMock()
        connection.__enter__.return_value = connection
        with patch("app.main.psycopg.connect", return_value=connection):
            response = ready()
        self.assertEqual(response["status"], "Healthy")
        connection.execute.assert_called_once_with("SELECT 1")

    def test_database_failure_returns_503_without_credentials(self):
        with patch("app.main.psycopg.connect", side_effect=psycopg.OperationalError("secret-password")):
            response = ready()
        self.assertEqual(response.status_code, 503)
        self.assertNotIn(b"secret-password", response.body)


if __name__ == "__main__":
    unittest.main()
