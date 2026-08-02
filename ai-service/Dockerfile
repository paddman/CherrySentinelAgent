FROM python:3.11-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    CHERRY_HOST=0.0.0.0 \
    CHERRY_PORT=8088 \
    CHERRY_DATABASE_PATH=/data/sentinel-brain.db \
    CHERRY_PLAYBOOK_DIR=/app/playbooks

WORKDIR /app

COPY pyproject.toml README.md ./
COPY app ./app
COPY playbooks ./playbooks

RUN pip install --upgrade pip && pip install . && \
    useradd --system --uid 10001 --create-home cherry && \
    mkdir -p /data && chown -R cherry:cherry /data /app

USER cherry
EXPOSE 8088

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8088/health', timeout=3)" || exit 1

CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8088", "--workers", "1"]
