"""Extended Sentinel Brain application with NT Shield product-profile routes."""

from .main import app
from .nt_shield import router as nt_shield_router
from .routes_v2 import router as extended_router

app.include_router(extended_router)
app.include_router(nt_shield_router)
