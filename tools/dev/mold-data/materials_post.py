# Post-processing for data/materials.json (design 1.5.1 + critique A5, A10, C3, C5). gen_materials.py builds the research
# rows unchanged; this adds the fields the page and the calculator need, validates the table and writes the shipped file.
import json, os, sys

GENERATED = "2026-10-19"   # fixed so --check is reproducible; change it when the data changes

# Process class (design D4) and whether v1 builds the mold (critique A10: v1 builds flash-type molds for classes A and B;
# class C - positive molds for PEEK/PAEK, PTFE preforms, UHMWPE - gets an advisory plan only).
PROCESS = {
    "liquid_cast_hot":           ("A", "Liquid pour (hot cast)"),
    "liquid_cast_rt":            ("A", "Liquid pour (room temperature)"),
    "foam_pour":                 ("A", "Foam pour"),
    "rubber_compression":        ("B", "Rubber compression"),
    "compound_compression":      ("B", "Compound compression"),
    "liquid_compression":        ("B", "LSR compression"),
    "thermoplastic_compression": ("C", "Thermoplastic compression"),
    "preform_sinter":            ("C", "Preform + sinter"),
}

# Shrink basis codes and the page choice each one defaults (critique A5). The calculator turns them into the scale factor.
SHRINK_BASIS = {
    "cold_mold_cold_part_steel_ref": "Supplier datasheet (steel mold)",
    "hot_cavity_cold_part": "Hot cavity to cold part",
    "measured_in_my_molds": "Measured in my molds at this temperature",
    "rt_mold_rt_part": "Supplier datasheet (steel mold)",
    "preform_die_rt_to_sintered_part_rt": "Measured in my molds at this temperature",
}

# Short page labels (critique C5: the PropertyManager is about 300 px wide). The full name stays in the tooltip/info line.
FAMILY_SHORT = {
    "pu_tdi_ether": "PU TDI-ether (MBOCA)", "pu_mdi_ether": "PU MDI-ether (BDO)", "pu_mdi_ester": "PU MDI-ester (BDO)",
    "pu_tdi_ester": "PU TDI-ester (MBOCA)", "pu_ppdi": "PU PPDI high-perf.", "pu_rt_cast": "PU room-temp. cast",
    "pu_rigid_structural": "PU rigid / structural", "pu_foam": "PU foam", "pu_millable": "PU millable rubber",
    "epoxy_casting": "Epoxy casting", "epoxy_filled": "Epoxy filled / tooling", "epoxy_emc": "Epoxy molding compound",
    "up_bmc": "Polyester BMC", "up_smc": "Polyester SMC", "up_casting": "Polyester casting", "vinylester": "Vinyl ester",
    "phenolic": "Phenolic compound", "melamine": "Melamine (MF)", "urea_formaldehyde": "Urea-formaldehyde (UF)",
    "dap": "DAP compound", "silicone_hcr": "Silicone HCR", "silicone_lsr": "Silicone LSR",
    "silicone_fvmq": "Fluorosilicone FVMQ", "silicone_rtv": "Silicone RTV casting", "rubber_nr": "Natural rubber NR",
    "rubber_sbr": "SBR", "rubber_epdm": "EPDM", "rubber_nbr": "Nitrile NBR", "rubber_hnbr": "HNBR",
    "rubber_cr": "Neoprene CR", "rubber_fkm": "FKM / FFKM", "rubber_acm": "ACM / AEM", "rubber_iir": "Butyl IIR / halobutyl",
    "rubber_eco": "Epichlorohydrin ECO", "tp_uhmwpe": "UHMWPE", "tp_ptfe": "PTFE preform", "tp_paek": "PEEK / PAEK",
    "tp_pvc_plastisol": "PVC plastisol",
}
GRADE_SHORT = {
    "pu-ppdi-90a": "PPDI 90A", "pu-ppdi-95a": "PPDI 95A / 50D",
    "pu-rt-elastomer-60-90a": "RT elastomer 60-90A", "pu-rt-rigid-casting": "RT rigid 70-80D",
    "pu-rigid-hotcast-80d": "Hot-cast rigid 80-85D", "pu-structural-foam": "Structural foam",
    "pu-foam-flexible-molded": "Flexible molded foam", "pu-foam-integral-skin": "Integral-skin foam",
    "pu-foam-microcellular": "Microcellular", "pu-millable-70a": "Millable PU 70A",
    "epoxy-cast-anhydride": "Anhydride heat-cure", "epoxy-cast-amine-rt": "Amine RT cure",
    "epoxy-tooling-al-filled": "Al-filled tooling", "epoxy-silica-apg": "Silica-filled APG",
    "epoxy-emc-silica": "EMC silica", "epoxy-emc-glass": "EMC glass",
    "up-bmc-standard": "BMC standard", "up-bmc-lowshrink": "BMC low-shrink",
    "up-smc-standard": "SMC standard", "up-smc-lp-classa": "SMC LP Class A",
    "up-cast-clear": "Clear casting", "up-cast-filled": "Filled casting",
    "ve-bmc": "VE BMC", "ve-smc": "VE SMC", "ve-casting": "VE casting resin",
    "phenolic-gp-woodflour": "General purpose", "phenolic-glass": "Glass-filled", "phenolic-mineral": "Mineral-filled",
    "phenolic-impact-cotton": "Impact (cotton)", "mf-cellulose": "Cellulose-filled", "mf-glass-mineral": "Glass/mineral-filled",
    "uf-cellulose": "Cellulose-filled", "dap-glass": "Glass-filled (SDG)", "dap-mineral": "Mineral-filled (MDG)",
    "si-fvmq-60a": "FVMQ 60A", "si-rtv-casting": "RTV casting",
    "epdm-s-60a": "EPDM sulfur 60A", "epdm-s-70a": "EPDM sulfur 70A", "epdm-s-80a": "EPDM sulfur 80A",
    "epdm-p-70a": "EPDM peroxide 70A", "nbr-s-70a": "NBR sulfur 70A", "nbr-s-90a": "NBR sulfur 90A",
    "nbr-p-70a": "NBR peroxide 70A", "fkm-bisphenol-75a": "FKM bisphenol 75A", "fkm-bisphenol-90a": "FKM bisphenol 90A",
    "fkm-peroxide-75a": "FKM peroxide 75A", "ffkm-75a": "FFKM 75A", "xiir-60a": "CIIR/BIIR 60A",
    "uhmwpe-virgin": "Virgin powder", "uhmwpe-filled": "Filled / modified",
    "ptfe-virgin": "Virgin granular", "ptfe-glass25": "25% glass", "ptfe-carbon-graphite": "25% carbon/graphite",
    "peek-450pf": "PEEK 450PF powder", "paek-ht-st": "PAEK HT / ST", "peek-milled-fibre": "PEEK + milled fibre",
    "pvc-plastisol": "Plastisol",
}
# Prefix rules for the regular grade series (e.g. "pu-tdi-ether-90a" -> "TDI-ether 90A").
SERIES = [("pu-tdi-ether-", "TDI-ether "), ("pu-mdi-ether-", "MDI-ether "), ("pu-mdi-ester-", "MDI-ester "),
          ("pu-tdi-ester-", "TDI-ester "), ("si-hcr-", "HCR "), ("si-lsr-", "LSR "), ("nr-", "NR "), ("sbr-", "SBR "),
          ("hnbr-", "HNBR "), ("cr-", "CR "), ("acm-", "ACM "), ("aem-", "AEM "), ("iir-", "IIR "), ("eco-", "ECO ")]

SOURCES = {
    "S1": ["Victrex, Compression Molding Processing Guide (Jan 2022)", "https://www.victrex.com/-/media/downloads/technical-guides/victrex_compression-molding-brochure_jan2022.pdf"],
    "S2": ["TSE Industries, Molding of Millathane Millable Urethane Rubber, Tips 2-2 Rev 1", "https://www.tse-industries.com/wp-content/uploads/Millathane-Molding-Tips2-2-Rev1.pdf"],
    "S3": ["Clippard, Material Properties Chart", "https://www.clippard.com/cms/node/583"],
    "S4": ["Precision Associates, Size & Tolerance catalog", "https://catalog.precisionassoc.com/Asset/PAI%20Size%20and%20Tol.pdf"],
    "S5": ["Apple Rubber, Hot topics for engineers: shrink", "https://www.applerubber.com/hot-topics-for-engineers/?p=1803"],
    "S6": ["Bakelite (Hexion) PF datasheets, Material Data Center", "https://www.materialdatacenter.com/ms/en/Bakelite/Hexion/Bakelite%C2%AE+PF+31%252E5/9e0795bd/295"],
    "S7": ["LyondellBasell BMC/SMC technical datasheets", "https://www.lyondellbasell.com"],
    "S8": ["SMC process data: EP0103954; Open University Manupedia, compression moulding", "https://www.open.edu/openlearn/science-maths-technology/engineering-technology/manupedia/compression-moulding"],
    "S9": ["Melamine resin patents US 4,303,561 / 4,328,326", "https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/4303561"],
    "S10": ["MakeItFrom, Melamine Formaldehyde Moulding Compound", "https://www.makeitfrom.com/material-properties/Melamine-Formaldehyde-Moulding-Compound"],
    "S11": ["Raschig DAP 5562 TDB; Professional Plastics MIL-M-14 overview", "https://professionalplastics.com/MILM14"],
    "S12": ["Dow SILASTIC SH 502 U / Shin-Etsu SV-15001U / Wacker ELASTOSIL R 701/40 S datasheets", "https://www.dow.com/en-us/pdp.silastic-sh-502-u-silicone-rubber.02039079h.html"],
    "S13": ["Simtec LSR shrinkage; Dow Liveo C6-550; Momentive Silopren LSR", "https://www.simtec-silicone.com/blogs/lsr-parts-expansion-or-shrinkage-of-liquid-silicone-rubber-lsr/"],
    "S14": ["Chemours Teflon granular; AGC Fluon G307/G355; Inoflon; Halopolymer PN90; 3M TF-1620", "https://www.teflon.com/Products/resins/ptfe-granular"],
    "S15": ["UHMWPE compression molding: RMUTT study; US 6,475,094", "https://research.rmutt.ac.th/?p=14988"],
    "S16": ["FKM mold fouling/corrosion: US 4,957,975; US 4,663,395 / 4,673,715", "https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/4957975"],
    "S17": ["EMC: CAPLINQ datasheet; US 6,319,739; US 9,890,307", "https://storefront.caplinq.com/products/6830/files/4567"],
    "S18": ["Room-temperature cast PU: BJB M-3190 / TC-680; Xometry urethane casting tips", "https://www.xometry.com/resources/casting/top-5-tips-for-designing-custom-urethane-casted-parts/"],
    "S19": ["LANXESS Adiprene TDI / LF TDI", "https://lanxess.com/en/products-and-brands/brands/adiprene/adiprene-tdi-and-lf-tdi"],
    "S20": ["US 10,766,171 (PU shrink 1-4% during molding and cure)", "https://patents.google.com/patent/US10766171"],
    "S21": ["Mearthane, cast molding design tips", "https://mearthane.com/5-design-tips-for-cast-molding-with-thermoset-polyurethanes/"],
    "S22": ["Goodyear Rubber design guide; Robinson Rubber, Designing with Rubber", "https://robinsonrubber.com/wp-content/uploads/2024/02/RRPC-Designing-With-Rubber.pdf"],
    "K-PU": ["Knowledge-based: cast PU supplier datasheets (Adiprene/Vibrathane/Andur class), handbook ranges", None],
    "K-EP": ["Knowledge-based: epoxy casting and molding compound datasheets, handbook ranges", None],
    "K-TS": ["Knowledge-based: Modern Plastics Encyclopedia / Plastics Design Library thermoset tables", None],
    "K-RUB": ["Knowledge-based: RMA Rubber Handbook, ISO 3302-1, typical compound datasheets", None],
    "K-TP": ["Knowledge-based: thermoplastic compression molding practice", None],
}

# Paths inputs.json (Track E) fills from, per the build contract (Track B's "|unit" suffix allowed). A null value is fine,
# a missing key is not (design 1.5.1 item 5).
FILL_PATHS = ["shrinkPct.typ", "sg.typ", "viscosityCp.typ", "mooney.typ", "potLifeMin.typ", "demoldMin.typ", "draftDeg.min",
              "moldTempC.typ", "moldingPressurePsi.typ|psi", "flashAllowancePct.typ", "shrinkBasisChoice", "processLabel",
              "confidence"]


def short_of(row):
    if row["id"] in GRADE_SHORT:
        return GRADE_SHORT[row["id"]]
    for prefix, label in SERIES:
        if row["id"].startswith(prefix):
            return label + row["id"][len(prefix):].upper()
    raise ValueError("no short label for " + row["id"])


def hot_cast_pu(row):
    # critique A5: the hot-cast PU rows' own notes describe contraction from a 100-120 C metal mold (hot-cavity basis)
    return row["group"] == "polyurethane" and row["processType"] == "liquid_cast_hot"


def finish(rows):
    by_family = {}
    for d in rows:
        cls, label = PROCESS[d["processType"]]
        d["processClass"] = cls
        d["processLabel"] = label
        d["builtByV1"] = cls in ("A", "B")
        d["familyShort"] = FAMILY_SHORT[d["family"]]
        d["short"] = short_of(d)
        if hot_cast_pu(d):
            d["shrinkBasis"] = "hot_cavity_cold_part"
            d["shrinkBasisConfidence"] = "estimate"
        else:
            d["shrinkBasisConfidence"] = "as published" if d["confidence"] in ("high", "medium") else "estimate"
        d["shrinkBasisChoice"] = SHRINK_BASIS[d["shrinkBasis"]]
        d["shrinkPctAxis"] = None   # {x, y, z} later for SMC/BMC, PEEK, UHMWPE, PTFE; the agent asks until then
        d["isDefault"] = False
        by_family.setdefault(d["family"], []).append(d)
    for members in by_family.values():
        pick = next((m for m in members if m["id"] == "pu-tdi-ether-90a"), None) \
            or next((m for m in members if m["confidence"] in ("medium", "high")), None) or members[0]
        pick["isDefault"] = True
    return rows


def validate(rows):
    ids = [d["id"] for d in rows]
    assert len(ids) == len(set(ids)), "duplicate ids"
    keys0 = list(rows[0].keys())
    for d in rows:
        assert list(d.keys()) == keys0, ("key set differs", d["id"])
        assert d["processType"] in PROCESS, d["id"]
        for s in d["sources"]:
            assert s in SOURCES, (d["id"], "unknown source", s)
        ranges = [(k, v) for k, v in d.items() if isinstance(v, dict)] + [("flash." + k, v) for k, v in (d["flash"] or {}).items() if isinstance(v, dict)]
        for k, v in ranges:
            if all(isinstance(v.get(x), (int, float)) for x in ("min", "typ", "max")):
                assert v["min"] <= v["typ"] <= v["max"], (d["id"], k, v)
    for fam in set(d["family"] for d in rows):
        assert sum(1 for d in rows if d["family"] == fam and d["isDefault"]) == 1, fam
        shorts = [d["short"] for d in rows if d["family"] == fam]
        assert len(shorts) == len(set(shorts)), ("short labels repeat in family", fam)
    for path in FILL_PATHS:
        for d in rows:
            node = d
            parts = path.split("|")[0].split(".")
            for i, part in enumerate(parts):
                assert isinstance(node, dict) and part in node, (d["id"], path)
                node = node[part]
                if node is None:
                    break   # a null parent (e.g. viscosityCp for rubber) hides the field: allowed


def document(rows):
    return {
        "schema": "solidworksAgent.part-materials/1",
        "generated": GENERATED,
        "generator": "tools/dev/mold-data/gen_materials.py",
        "definitions": {
            "processClass": {"A": "liquid pour (liquid_cast_hot, liquid_cast_rt, foam_pour): flash mold, full-face metal-to-metal land",
                             "B": "compression (rubber_compression, compound_compression, liquid_compression): flash mold with stop pads and overflow",
                             "C": "positive (thermoplastic_compression, preform_sinter): advisory plan only - not built by this tool in v1"},
            "builtByV1": "false for class C rows: kept as advisory data (critique A10/G2)",
            "viscosityClass": {"liquid_low": "free-flowing pour, < about 3,000 cP at the process temperature",
                               "liquid_med": "pourable, about 3,000-10,000 cP", "liquid_high": "heavy pour or paste, > 10,000 cP",
                               "paste_lsr": "liquid silicone (pumped paste)", "rubber": "rubber compound (Mooney)",
                               "rubber_stiff": "stiff rubber compound", "compound": "thermoset molding compound",
                               "sheet_bulk": "SMC/BMC", "melt": "thermoplastic melt", "no_flow": "does not flow (sinter / positive)",
                               "foam": "expanding foam",
                               "note": "For liquids the calculator uses the numeric viscosity from the page, not this class."},
            "shrinkBasis": {
                "cold_mold_cold_part_steel_ref": "Supplier datasheet (steel mold): s = 1/(1-S) x (1 + 12e-6 dT)/(1 + a_cavity dT)",
                "hot_cavity_cold_part": "Hot cavity to cold part: s = 1/((1-S)(1 + a_cavity dT))",
                "measured_in_my_molds": "Measured in my molds at this temperature: s = 1/(1-S), no metal term",
                "rt_mold_rt_part": "Room-temperature mold and part: s = 1/(1-S)",
                "preform_die_rt_to_sintered_part_rt": "Preform die at room temperature to sintered part: s = 1/(1-S) plus 1.5-3 mm machining stock per side; press temperature ignored",
                "v1_style": "v1 style, s = 1 + S (only to match molds built with v1)"},
            "shrinkBasisChoice": "the page's shrink_basis option this row defaults to",
            "shrinkBasisConfidence": "estimate = the basis itself is uncertain (hot-cast PU: notes describe contraction from a hot metal mold)",
            "isDefault": "one row per family: the grade the page picks first",
            "short / familyShort": "page labels (about 300 px wide); the full name goes in the tooltip and info line",
        },
        "sources": {k: {"title": v[0], "url": v[1]} for k, v in SOURCES.items()},
        "rows": rows,
    }


def main(out):
    rows = finish(out)
    validate(rows)
    text = json.dumps(document(rows), indent=1, ensure_ascii=True) + "\n"
    here = os.path.dirname(os.path.abspath(__file__))
    path = os.path.normpath(os.path.join(here, "..", "..", "builtin", "design-compression-mold", "data", "materials.json"))
    if "--check" in sys.argv:
        with open(path, "r", encoding="utf-8") as f:
            same = f.read() == text
        print("materials.json", "matches" if same else "DIFFERS from the generator")
        sys.exit(0 if same else 1)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print(len(rows), "subtypes;", len(set(d["family"] for d in rows)), "families ->", path)
