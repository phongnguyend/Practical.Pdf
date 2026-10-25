# ExcelToPdf.Playground

React 19 playground for `.xlsx` / `.xls` files up to 10 MB.
Upload or drag a file, convert through `ExcelToPdf.Proxy`, then view the PDF in a
new tab or download it. LibreOffice is the initial conversion engine.

```powershell
npm ci
npm run dev
```

Open http://localhost:5175. Set `EXCEL_TO_PDF_PROXY_URL` in `.env` (see `.env.sample`)
to change the development proxy target. Default: http://localhost:62723.
`npm run build` builds the frontend; `npm run test:e2e` runs browser tests.
See [the solution README](../README.md) for backend and Docker instructions.
