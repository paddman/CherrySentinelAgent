# Cherry Sentinel Agent — GUI Pack

แพ็กนี้จัดทำจาก Cherry ที่ส่งมา และ mockup Dashboard ที่ออกแบบในแชต เพื่อเอาไปต่อเข้ากับแอป Windows ได้ทันที

## ของที่อยู่ใน ZIP

- `wpf/CherrySentinel.sln` — ตัวอย่าง GUI WPF สำหรับ Visual Studio
- `wpf/CherrySentinel.Gui/` — XAML, C# code-behind และ assets ที่ embed เป็น Resource
- `assets/cherry/` — Cherry แบบตัดพื้นหลัง, avatar, logo และ pose จาก dashboard
- `assets/ui-reference/panels/` — ภาพที่ตัดเป็นส่วน Sidebar, Cards, Chart, Incident Detail และ Quick Actions
- `assets/icons/` — SVG icon สำหรับเมนูและปุ่มสำคัญ
- `preview/index.html` — ตัวอย่าง UI ที่เปิดดูใน Browser ได้
- `preview/preview.png` — ภาพตัวอย่าง GUI ที่ render จาก HTML
- `docs/` — วิธีต่อเข้ากับ Agent/API

## เปิด WPF GUI

1. เปิด `wpf/CherrySentinel.sln` ด้วย Visual Studio 2019/2022
2. ตรวจว่ามี .NET Framework 4.8 Developer Pack
3. Build และ Run โปรเจกต์ `CherrySentinel.Gui`
4. เปลี่ยนข้อมูลตัวอย่างใน `MainWindow.xaml.cs` เป็น ViewModel/API จริง

## จุดที่ต้องต่อกับระบบจริง

- Summary cards: heartbeat และ incident counts
- Active incidents: `/api/v1/incidents`
- Failed Login chart: event 4624/4625 aggregation
- Incident detail: incident ที่เลือก
- Block/Capture/Quarantine/Export: Response API

ปุ่ม Response ใน starter นี้เป็น demo และไม่สั่ง block จริง เพื่อป้องกันการกระทบระบบโดยไม่ตั้งใจ
