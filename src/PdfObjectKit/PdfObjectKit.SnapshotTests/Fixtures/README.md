# Markdown regression fixture

`input.pdf` is copied unchanged from `PdfObjectKit.Cli/bin/input.pdf`.
It contains the four-page Contoso invoice, mock bank statement, and loan application used during converter development.

Keep this PDF stable. Review changes to both the fixture and its verified Markdown together.

`images.pdf` is a synthetic two-page fixture. Its first page draws one RGB image XObject twice (including a rotated placement), an inline RGB image, and the shared XObject inside a transformed Form XObject. Its second page is blank. It contains no external assets or private data.

To regenerate it deterministically, run `python generate_images.py` from this directory. Python is only needed for regeneration; tests read the committed PDF directly.
