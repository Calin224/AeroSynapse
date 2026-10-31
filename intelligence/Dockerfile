FROM python:3.13-slim AS runtime
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /app
COPY intelligence/requirements.lock ./
RUN pip install --no-cache-dir -r requirements.lock && useradd --create-home --uid 10001 app
COPY intelligence/app/ ./app/
COPY contracts/ ./contracts/
COPY intelligence/docker-entrypoint.sh ./
RUN chmod +x docker-entrypoint.sh
USER app
EXPOSE 8000
ENTRYPOINT ["./docker-entrypoint.sh"]

FROM runtime AS contract-tests
USER root
ENV PYTHONPATH=/app:/app/intelligence
COPY intelligence/requirements-dev.lock ./
RUN pip install --no-cache-dir -r requirements-dev.lock
COPY intelligence/tests/ ./intelligence/tests/
COPY intelligence/examples/ ./intelligence/examples/
COPY backend/examples/ ./backend/examples/
ENTRYPOINT ["python", "-m", "pytest", "-q", "/app/intelligence/tests", "/app/contracts/tests", "-o", "cache_dir=/tmp/pytest-cache"]

FROM contract-tests AS critical-tests
COPY intelligence/requirements-ci.lock intelligence/ruff.toml ./
RUN pip install --no-cache-dir -r requirements-ci.lock
COPY tests/critical/ ./critical/
COPY scripts/critical-pytest.py ./critical-pytest.py
RUN ruff check --no-cache --config ruff.toml app intelligence/tests contracts/tests critical critical-pytest.py
ENTRYPOINT ["python", "/app/critical-pytest.py"]
