#!/usr/bin/env python3
"""Converts the macOS app's printer models (Resources/Printers/<shape>.usdz, built in Blender) into the small mesh files
the Windows app draws with WPF 3D (src/BambuBar/Resources/Printers/<shape>.mesh.json.gz).

    python3 -m venv .venv && .venv/bin/pip install usd-core
    .venv/bin/python tools/convert-models.py <bambu-bar checkout>/Resources/Printers src/BambuBar/Resources/Printers

Keeps the node tree (names, local transforms) because the app finds and moves nodes by name (Part_00…, Spool_0,
Filament_0, Toolhead, Bed, BedCarriage, Gantry, Lamp, AMS), triangulates the faces, and keeps each material's
UsdPreviewSurface inputs (no textures are used)."""
import gzip, json, os, sys
from pxr import Usd, UsdGeom, UsdShade


def r(values, digits=5):
    return [round(float(v), digits) for v in values]


def mesh_data(prim):
    mesh = UsdGeom.Mesh(prim)
    points = mesh.GetPointsAttr().Get() or []
    counts = mesh.GetFaceVertexCountsAttr().Get() or []
    indices = mesh.GetFaceVertexIndicesAttr().Get() or []
    if not points or not counts:
        return None
    normals = mesh.GetNormalsAttr().Get()
    interpolation = mesh.GetNormalsInterpolation() if normals else None
    primvars = UsdGeom.PrimvarsAPI(prim)
    if not normals and primvars.HasPrimvar("normals"):
        pv = primvars.GetPrimvar("normals")
        normals = pv.ComputeFlattened()
        interpolation = pv.GetInterpolation()
    left = mesh.GetOrientationAttr().Get() == UsdGeom.Tokens.leftHanded

    positions, out_normals, triangles = [], [], []
    # One vertex per face corner: normals in these exports are face-varying (hard edges), and it keeps the format simple.
    corner = 0
    for face, count in enumerate(counts):
        base = len(positions) // 3
        for k in range(count):
            p = points[indices[corner + k]]
            positions += [p[0], p[1], p[2]]
            if normals is not None and interpolation in ("faceVarying",):
                n = normals[corner + k]
            elif normals is not None and interpolation in ("vertex", "varying"):
                n = normals[indices[corner + k]]
            elif normals is not None and interpolation == "uniform":
                n = normals[face]
            else:
                n = None
            out_normals += [n[0], n[1], n[2]] if n is not None else [0, 0, 0]
        for k in range(1, count - 1):
            tri = [base, base + k, base + k + 1]
            triangles += [tri[0], tri[2], tri[1]] if left else tri
        corner += count
    material, _ = UsdShade.MaterialBindingAPI(prim).ComputeBoundMaterial()
    data = {"mat": material.GetPrim().GetName() if material else "", "p": r(positions), "i": triangles}
    if normals is not None:
        data["n"] = r(out_normals, 4)
    if mesh.GetDoubleSidedAttr().Get():
        data["double"] = True
    return data


def node(prim):
    out = {"name": prim.GetName()}
    xform = UsdGeom.Xformable(prim)
    if xform:
        m = xform.GetLocalTransformation()
        flat = [m[row][col] for row in range(4) for col in range(4)]
        if flat != [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]:
            out["m"] = r(flat, 6)
    if prim.GetTypeName() == "Mesh":
        data = mesh_data(prim)
        if data:
            out["mesh"] = data
    children = [node(c) for c in prim.GetChildren() if c.GetTypeName() in ("Xform", "Mesh", "Scope") and c.GetName() != "_materials"]
    children = [c for c in children if c.get("mesh") or c.get("children")]
    if children:
        out["children"] = children
    return out


def materials(stage):
    found = {}
    for prim in stage.Traverse():
        if prim.GetTypeName() != "Material":
            continue
        for child in prim.GetChildren():
            shader = UsdShade.Shader(child)
            if shader and shader.GetIdAttr().Get() == "UsdPreviewSurface":
                values = {}
                for i in shader.GetInputs():
                    v = i.Get()
                    if v is None:
                        continue
                    values[i.GetBaseName()] = r(v) if hasattr(v, "__len__") else round(float(v), 5)
                found[prim.GetName()] = values
    return found


def convert(source, target):
    stage = Usd.Stage.Open(source)
    root = stage.GetDefaultPrim() or stage.GetPseudoRoot().GetChildren()[0]
    data = {"upAxis": UsdGeom.GetStageUpAxis(stage), "metersPerUnit": UsdGeom.GetStageMetersPerUnit(stage),
            "materials": materials(stage), "root": node(root)}
    with gzip.open(target, "wt", encoding="utf-8", compresslevel=9) as f:
        json.dump(data, f, separators=(",", ":"))


def main():
    source, target = sys.argv[1], sys.argv[2]
    os.makedirs(target, exist_ok=True)
    for name in sorted(os.listdir(source)):
        if name.endswith(".usdz"):
            out = os.path.join(target, name[:-5] + ".mesh.json.gz")
            convert(os.path.join(source, name), out)
            print(f"{name} -> {out} ({os.path.getsize(out) // 1024} KB)")


if __name__ == "__main__":
    main()
