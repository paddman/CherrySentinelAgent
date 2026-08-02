"""Extended Sentinel Brain application with bounded investigation and IOC exchange routes."""

from .main import app
from .routes_v2 import router

app.include_router(router)
