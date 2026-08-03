"""
Merge FIX_Latest_EP302.xml (base) + 9_FIX44_FP_QF.xml (FixPortal additions)
into 10_FIX50_FP_QF.xml.

Rules:
- EP302 authoritative for name/type on overlapping fields
- FIX44 extra enums appended to EP302 base enums
- FIX44-only fields carried verbatim
- Where EP302 has inline groups with corresponding FIX44 wrapper components,
  use the component reference and update wrapper contents to EP302's expanded fields
- Admin messages excluded
- Known overrides applied (tag 47, 327, 800, OrigClOrdID, etc.)
"""

import os
import xml.etree.ElementTree as ET
from defusedxml.ElementTree import parse as safe_parse
from collections import OrderedDict
import copy

# Paths are resolved relative to this script:
#   script lives in spec/FixPortal/Extension Pack Support/orchestra/
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
EXT_PACK_DIR = os.path.dirname(SCRIPT_DIR)          # spec/FixPortal/Extension Pack Support
FP_SPEC_DIR = os.path.dirname(EXT_PACK_DIR)         # spec/FixPortal

# ─── Parse helpers ───

def parse_fields(root):
    """Return {tag_num: element} for all fields."""
    fields = OrderedDict()
    for f in root.find("fields"):
        tag = int(f.get("number"))
        fields[tag] = f
    return fields

def parse_components(root):
    """Return {name: element} for all components."""
    comps = OrderedDict()
    section = root.find("components")
    if section is not None:
        for c in section:
            comps[c.get("name")] = c
    return comps

def parse_messages(root):
    """Return {msgtype: element} for all messages."""
    msgs = OrderedDict()
    for m in root.find("messages"):
        msgs[m.get("msgtype")] = m
    return msgs

def get_enums(field_elem):
    """Return {enum_value: description} for a field's value elements."""
    enums = OrderedDict()
    for v in field_elem.findall("value"):
        enums[v.get("enum")] = v.get("description", "")
    return enums

def get_child_names(elem):
    """Return set of (tag, name) for direct children (field/component/group)."""
    names = set()
    for child in elem:
        name = child.get("name")
        if name:
            names.add((child.tag, name))
    return names

def collect_group_counter_to_wrapper(fix44_comps):
    """
    Build mapping: group_counter_field_name -> wrapper_component_name.
    A wrapper component has exactly one top-level child which is a <group>.
    """
    mapping = {}
    for comp_name, comp_elem in fix44_comps.items():
        children = list(comp_elem)
        if len(children) == 1 and children[0].tag == "group":
            group_name = children[0].get("name")
            mapping[group_name] = comp_name
        elif len(children) >= 1:
            # Some wrappers have a group plus additional fields at the same level
            # Check if first child is a group
            groups = [c for c in children if c.tag == "group"]
            if len(groups) == 1 and groups[0] == children[0]:
                group_name = groups[0].get("name")
                # Only map if this looks like a wrapper (group is primary content)
                if comp_name not in mapping:
                    mapping[group_name] = comp_name
    return mapping


# ─── Merge fields ───

KNOWN_NO_CARRY_ENUMS = {327, 800}  # Don't carry FIX44 enums for these tags
TAG47_TYPE_OVERRIDE = "STRING"  # Rule80A

def merge_fields(ep302_fields, fix44_fields):
    """Merge fields. Returns OrderedDict {tag: element}."""
    merged = OrderedDict()
    all_tags = sorted(set(ep302_fields.keys()) | set(fix44_fields.keys()))

    for tag in all_tags:
        in_ep = tag in ep302_fields
        in_44 = tag in fix44_fields

        if in_ep and not in_44:
            merged[tag] = copy.deepcopy(ep302_fields[tag])
        elif in_44 and not in_ep:
            elem = copy.deepcopy(fix44_fields[tag])
            # Tag 47 override: force STRING
            if tag == 47:
                elem.set("type", TAG47_TYPE_OVERRIDE)
            merged[tag] = elem
        else:
            # Both — EP302 wins for name/type
            ep_elem = copy.deepcopy(ep302_fields[tag])

            if tag not in KNOWN_NO_CARRY_ENUMS:
                # Append FIX44 extra enums
                ep_enums = get_enums(ep_elem)
                f44_enums = get_enums(fix44_fields[tag])
                for val, desc in f44_enums.items():
                    if val not in ep_enums:
                        v = ET.SubElement(ep_elem, "value")
                        v.set("enum", val)
                        v.set("description", desc)

            merged[tag] = ep_elem

    return merged


# ─── Merge components ───

RETIRE_COMPONENTS = {"PartyAltIDGrp", "PartyAltSubGrp"}

def merge_components(ep302_comps, fix44_comps, group_to_wrapper, ep302_msgs):
    """
    Merge components.
    - EP302 components carried.
    - FIX44-only components carried (except retired ones).
    - For shared components: EP302 base + FIX44-only children appended.
    - For FIX44 group-wrappers that correspond to EP302 inline groups:
      update wrapper contents to EP302's expanded group content.
    """
    merged = OrderedDict()

    # Collect all inline groups from EP302 messages and components
    # keyed by group counter field name -> group element
    inline_groups = {}
    _collect_inline_groups(ep302_msgs, inline_groups)
    for comp_elem in ep302_comps.values():
        _collect_inline_groups_from_elem(comp_elem, inline_groups)

    # First, add all EP302 components
    for name, elem in ep302_comps.items():
        merged[name] = copy.deepcopy(elem)

    # Then process FIX44 components
    for name, elem in fix44_comps.items():
        if name in RETIRE_COMPONENTS:
            continue

        if name in merged:
            # Shared component — append FIX44-only children
            ep_elem = merged[name]
            ep_child_names = get_child_names(ep_elem)
            for child in elem:
                child_key = (child.tag, child.get("name"))
                if child_key not in ep_child_names:
                    new_child = copy.deepcopy(child)
                    new_child.set("required", "N")
                    ep_elem.append(new_child)
        else:
            # FIX44-only component
            f44_elem = copy.deepcopy(elem)

            # If it's a group-wrapper, check if EP302 has an expanded inline version
            children = list(f44_elem)
            if children and children[0].tag == "group":
                grp_name = children[0].get("name")
                if grp_name in inline_groups:
                    # Replace the wrapper's group content with EP302's expanded version
                    ep_grp = copy.deepcopy(inline_groups[grp_name])
                    # Preserve the required attribute from the FIX44 wrapper
                    req = children[0].get("required", "N")
                    ep_grp.set("required", req)

                    # But also append any FIX44-only children within the group
                    ep_grp_child_names = get_child_names(ep_grp)
                    for gc in children[0]:
                        gc_key = (gc.tag, gc.get("name"))
                        if gc_key not in ep_grp_child_names:
                            new_gc = copy.deepcopy(gc)
                            new_gc.set("required", "N")
                            ep_grp.append(new_gc)

                    # Replace the group in the wrapper
                    f44_elem.remove(children[0])
                    f44_elem.insert(0, ep_grp)

                    # Also append any FIX44-only non-group children of the wrapper
                    # (some wrappers have fields after the group)

            merged[name] = f44_elem

    return merged


def _collect_inline_groups(msgs_section, groups_dict):
    """Recursively collect all inline groups from messages."""
    if isinstance(msgs_section, dict):
        for msg_elem in msgs_section.values():
            _collect_inline_groups_from_elem(msg_elem, groups_dict)
    else:
        for msg_elem in msgs_section:
            _collect_inline_groups_from_elem(msg_elem, groups_dict)


def _collect_inline_groups_from_elem(elem, groups_dict):
    """Recursively collect inline groups from an element."""
    for child in elem:
        if child.tag == "group":
            gname = child.get("name")
            if gname:
                # Keep the largest version (most children)
                if gname not in groups_dict or len(list(child)) > len(list(groups_dict[gname])):
                    groups_dict[gname] = child
            _collect_inline_groups_from_elem(child, groups_dict)


# ─── Merge messages ───

ADMIN_MSGTYPES = {"0", "1", "2", "3", "4", "5", "A", "n"}
ORIGCLORDID_OVERRIDE_Y = {"F"}  # OrderCancelRequest: force required="Y"

def merge_messages(ep302_msgs, fix44_msgs, group_to_wrapper):
    """
    Merge messages.
    - EP302 base for shared messages, with FIX44 FixPortal additions appended.
    - Inline groups replaced with component refs where wrappers exist.
    - Admin messages excluded.
    - EP302-only messages carried (also with inline->component conversion).
    """
    merged = OrderedDict()

    # Process EP302 messages
    for msgtype, elem in ep302_msgs.items():
        merged_msg = copy.deepcopy(elem)
        merged_msg.set("msgcat", "app")

        # Replace inline groups with component references
        _replace_inline_with_components(merged_msg, group_to_wrapper)

        # If FIX44 has this message, append FixPortal additions
        if msgtype in fix44_msgs:
            f44_msg = fix44_msgs[msgtype]
            ep_child_names = get_child_names(merged_msg)
            # Also collect names from wrapper components (the groups they contain)
            ep_names_flat = {name for _, name in ep_child_names}

            for child in f44_msg:
                child_name = child.get("name")
                child_tag = child.tag
                child_key = (child_tag, child_name)

                # Skip if already present
                if child_key in ep_child_names:
                    continue

                # Skip if it's a group-wrapper component whose group is already present
                # (i.e., the inline group was already converted to this component ref)
                if child_name in ep_names_flat:
                    continue

                # Skip admin-related refs
                if child_name in ("StandardHeader", "StandardTrailer"):
                    continue

                # Skip FIX44 inline groups that have wrapper components
                # (the wrapper component ref should already be there)
                if child_tag == "group" and child_name in group_to_wrapper:
                    wrapper_name = group_to_wrapper[child_name]
                    if wrapper_name in ep_names_flat:
                        continue

                new_child = copy.deepcopy(child)
                new_child.set("required", "N")
                merged_msg.append(new_child)

        # Apply OrigClOrdID override
        if msgtype in ORIGCLORDID_OVERRIDE_Y:
            _set_field_required(merged_msg, "OrigClOrdID", "Y")

        merged[msgtype] = merged_msg

    # FIX44-only non-admin messages
    for msgtype, elem in fix44_msgs.items():
        if msgtype in merged or msgtype in ADMIN_MSGTYPES:
            continue
        f44_cat = elem.get("msgcat", "app")
        if f44_cat == "admin":
            continue
        merged_msg = copy.deepcopy(elem)
        merged_msg.set("msgcat", "app")
        _replace_inline_with_components(merged_msg, group_to_wrapper)
        merged[msgtype] = merged_msg

    return merged


def _replace_inline_with_components(elem, group_to_wrapper):
    """
    Recursively replace inline groups with component references
    where a FIX44 wrapper component exists.
    """
    children_to_remove = []
    children_to_add = []

    for i, child in enumerate(elem):
        if child.tag == "group":
            gname = child.get("name")
            if gname in group_to_wrapper:
                wrapper_name = group_to_wrapper[gname]
                req = child.get("required", "N")
                comp_ref = ET.Element("component")
                comp_ref.set("name", wrapper_name)
                comp_ref.set("required", req)
                children_to_remove.append(child)
                children_to_add.append((i, comp_ref))
            else:
                # Recurse into groups that don't have wrappers
                _replace_inline_with_components(child, group_to_wrapper)
        elif child.tag == "component":
            # Don't recurse into component refs (they're just references)
            pass

    # Apply replacements (reverse order to maintain indices)
    for child in children_to_remove:
        elem.remove(child)
    for i, comp_ref in reversed(children_to_add):
        elem.insert(i, comp_ref)


def _set_field_required(msg_elem, field_name, value):
    """Set required attribute on a field within a message (non-recursive)."""
    for child in msg_elem:
        if child.tag == "field" and child.get("name") == field_name:
            child.set("required", value)
            return
    # Also check inside groups
    for child in msg_elem:
        if child.tag == "group":
            _set_field_required(child, field_name, value)


# ─── Write output ───

def indent_xml(elem, level=0):
    """Add pretty-print indentation."""
    indent = "\n" + "\t" * level
    if len(elem):
        if not elem.text or not elem.text.strip():
            elem.text = indent + "\t"
        if not elem.tail or not elem.tail.strip():
            elem.tail = indent
        for i, child in enumerate(elem):
            indent_xml(child, level + 1)
        if not child.tail or not child.tail.strip():
            child.tail = indent
    else:
        if level and (not elem.tail or not elem.tail.strip()):
            elem.tail = indent
    if level == 0:
        elem.tail = "\n"


def write_output(path, merged_fields, merged_comps, merged_msgs):
    root = ET.Element("fix")
    root.set("major", "5")
    root.set("type", "FIX")
    root.set("servicepack", "2")
    root.set("minor", "0")
    root.set("name", "FIX50_FP_QF")

    # Header / Trailer (empty for app dictionary)
    ET.SubElement(root, "header")
    ET.SubElement(root, "trailer")

    # Messages
    msgs_elem = ET.SubElement(root, "messages")
    for msgtype in sorted(merged_msgs.keys(), key=lambda x: (len(x), x)):
        msgs_elem.append(merged_msgs[msgtype])

    # Components
    comps_elem = ET.SubElement(root, "components")
    for name in sorted(merged_comps.keys()):
        comps_elem.append(merged_comps[name])

    # Fields
    fields_elem = ET.SubElement(root, "fields")
    for tag in sorted(merged_fields.keys()):
        fields_elem.append(merged_fields[tag])

    indent_xml(root)

    tree = ET.ElementTree(root)
    ET.indent(tree, space="\t")
    tree.write(path, encoding="unicode", xml_declaration=False)

    # Prepend with proper formatting
    with open(path, "r", encoding="utf-8") as f:
        content = f.read()
    with open(path, "w", encoding="utf-8") as f:
        f.write(content)

    print(f"Written to {path}")


# ─── Main ───

def main():
    ep302_path = os.path.join(EXT_PACK_DIR, "FIX_Latest_EP302.xml")
    fix44_path = os.path.join(FP_SPEC_DIR, "9_FIX44_FP_QF.xml")
    output_path = os.path.join(FP_SPEC_DIR, "10_FIX50_FP_QF.xml")

    print("Parsing EP302...")
    ep302_tree = safe_parse(ep302_path)
    ep302_root = ep302_tree.getroot()

    print("Parsing FIX44 FixPortal...")
    fix44_tree = safe_parse(fix44_path)
    fix44_root = fix44_tree.getroot()

    # Parse sections
    ep302_fields = parse_fields(ep302_root)
    fix44_fields = parse_fields(fix44_root)
    ep302_comps = parse_components(ep302_root)
    fix44_comps = parse_components(fix44_root)
    ep302_msgs = parse_messages(ep302_root)
    fix44_msgs = parse_messages(fix44_root)

    print(f"EP302: {len(ep302_fields)} fields, {len(ep302_comps)} components, {len(ep302_msgs)} messages")
    print(f"FIX44: {len(fix44_fields)} fields, {len(fix44_comps)} components, {len(fix44_msgs)} messages")

    # Build group-to-wrapper mapping from FIX44
    group_to_wrapper = collect_group_counter_to_wrapper(fix44_comps)
    print(f"Group-to-wrapper mappings: {len(group_to_wrapper)}")

    # Merge
    print("Merging fields...")
    merged_fields = merge_fields(ep302_fields, fix44_fields)
    print(f"  Merged fields: {len(merged_fields)}")

    print("Merging components...")
    merged_comps = merge_components(ep302_comps, fix44_comps, group_to_wrapper, ep302_msgs)
    print(f"  Merged components: {len(merged_comps)}")

    print("Merging messages...")
    merged_msgs = merge_messages(ep302_msgs, fix44_msgs, group_to_wrapper)
    print(f"  Merged messages: {len(merged_msgs)}")

    print("Writing output...")
    write_output(output_path, merged_fields, merged_comps, merged_msgs)

    # Stats
    admin_count = sum(1 for mt in fix44_msgs if mt in ADMIN_MSGTYPES or fix44_msgs[mt].get("msgcat") == "admin")
    print(f"\nSummary:")
    print(f"  Fields: {len(merged_fields)}")
    print(f"  Components: {len(merged_comps)}")
    print(f"  Messages: {len(merged_msgs)} (admin excluded: {admin_count})")


if __name__ == "__main__":
    main()
