# FixPortal.QuickFIXn.DataDictionaries

The FixPortal-customised QuickFIX/n FIX data dictionaries (the `*_FP_QF.xml` set plus
the stock support dictionaries), shipped as NuGet content under `DataDictionary/`.

Consumers copy these to their build output and point their FIX session / dictionary
loader at the resulting folder. Content-only — no assemblies.
