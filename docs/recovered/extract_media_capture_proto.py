from pathlib import Path
from google.protobuf.descriptor_pb2 import FileDescriptorProto


TYPE_NAMES = {
    1: "double",
    2: "float",
    3: "int64",
    4: "uint64",
    5: "int32",
    6: "fixed64",
    7: "fixed32",
    8: "bool",
    9: "string",
    10: "group",
    11: "message",
    12: "bytes",
    13: "uint32",
    14: "enum",
    15: "sfixed32",
    16: "sfixed64",
    17: "sint32",
    18: "sint64",
}


def find_descriptor(path: Path) -> tuple[int, int, FileDescriptorProto]:
    data = path.read_bytes()
    marker = b"\x0a\x13media_capture.proto"
    start = data.find(marker)
    if start < 0:
        raise ValueError(f"{path}: media_capture.proto descriptor marker not found")

    candidates: list[tuple[tuple[int, int, int], int, FileDescriptorProto]] = []
    for end in range(start + len(marker) + 10, min(len(data), start + 4096)):
        descriptor = FileDescriptorProto()
        try:
            descriptor.ParseFromString(data[start:end])
        except Exception:
            continue

        if descriptor.name == "media_capture.proto" and descriptor.package and descriptor.message_type:
            score = (len(descriptor.message_type), len(descriptor.enum_type), end - start)
            candidates.append((score, end - start, descriptor))

    if not candidates:
        raise ValueError(f"{path}: descriptor marker was found but no complete descriptor parsed")

    _, size, descriptor = max(candidates, key=lambda item: item[0])
    return start, size, descriptor


def type_name(field) -> str:
    if field.type_name:
        return field.type_name.rsplit(".", 1)[-1]
    return TYPE_NAMES.get(field.type, f"type_{field.type}")


def to_proto(descriptor: FileDescriptorProto) -> str:
    lines = [
        'syntax = "proto2";',
        "",
        f"package {descriptor.package};",
        "",
    ]

    for message in descriptor.message_type:
        lines.append(f"message {message.name} {{")
        for field in message.field:
            lines.append(f"  optional {type_name(field)} {field.name} = {field.number};")
        lines.append("}")
        lines.append("")

    for enum in descriptor.enum_type:
        lines.append(f"enum {enum.name} {{")
        for value in enum.value:
            lines.append(f"  {value.name} = {value.number};")
        lines.append("}")
        lines.append("")

    return "\n".join(lines).rstrip() + "\n"


def main() -> None:
    for arg in [
        "SeewoCore/media_capture_client.dll",
        "SeewoCore/toolbox/rtcRemoteDesktop/media_capture_client.dll",
        "SeewoCore/toolbox/media_capture/media_capture.exe",
    ]:
        path = Path(arg)
        if not path.exists():
            continue
        offset, size, descriptor = find_descriptor(path)
        print(f"// {path} offset=0x{offset:x} size={size}")
        print(to_proto(descriptor))


if __name__ == "__main__":
    main()
