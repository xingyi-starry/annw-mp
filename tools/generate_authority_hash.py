"""Regenerate explicit C#/Go authority field encoders. Requires Python 3 and protoc.
Run from any directory; optional argument selects the Relay repository.
Generated encoders never use protobuf serialization or reflection at runtime.
"""
from pathlib import Path
import re,json,subprocess,tempfile,sys
r=Path(__file__).resolve().parent.parent
proto=r/'src/XingyiStarry.Mp.Protocol/Protos/multiplayer.proto'
with tempfile.TemporaryDirectory(prefix='mp-hash-') as temp:
 descriptor=Path(temp)/'schema.bin'
 subprocess.run(['protoc','--proto_path='+str(proto.parent),'--descriptor_set_out='+str(descriptor),str(proto)],check=True)
 descriptor_bytes=descriptor.read_bytes()
# Minimal descriptor reader; avoids adding a source dependency for code generation.
def parse(b):
 d={};i=0
 def var():
  nonlocal i
  v=0;n=0
  while True:
   x=b[i];i+=1;v|=(x&127)<<n;n+=7
   if x<128:return v
 while i<len(b):
  tag=var();w=tag&7;k=tag>>3
  if w==0:v=var()
  elif w==2:n=var();v=b[i:i+n];i+=n
  elif w==1:v=b[i:i+8];i+=8
  elif w==5:v=b[i:i+4];i+=4
  else:raise ValueError(w)
  d.setdefault(k,[]).append(v)
 return d
def one(d,k,default=None):return d.get(k,[default])[0]
def st(d,k):return one(d,k,b'').decode()
def pas(n):return ''.join(x[:1].upper()+x[1:] for x in n.split('_'))
file=parse(one(parse(descriptor_bytes),1));messages={}
for b in file[4]:
 d=parse(b);name=st(d,1);fields=[]
 for bf in d.get(2,[]):
  f=parse(bf);opts=parse(one(f,8,b''))
  if one(opts,3,0):continue
  fields.append({'name':st(f,1),'num':one(f,3),'label':one(f,4),'type':one(f,5),'typename':st(f,6).split('.')[-1], 'optional':one(f,17,0), 'group':one(f,9)})
 messages[name]=(sorted(fields,key=lambda f:f['num']),[st(parse(x),1) for x in d.get(8,[])])
# Derive names and explicit branch numbers from the canonical schema.
frame_fields=messages['AuthorityFrameMessage'][0]
maps={'frames':{pas(f['name']):f['typename'] for f in frame_fields if f['group'] is not None},
      'frame_ids':[(pas(f['name']),f['num']-100) for f in frame_fields if f['group'] is not None]}
# Only generate the graph reachable from authority frames.
needed=set()
def visit(name):
 if name in needed:return
 needed.add(name)
 for f in messages[name][0]:
  if f['type']==11:visit(f['typename'])
visit('AuthorityFrameMessage')
messages={k:v for k,v in messages.items() if k in needed}

def csval(f,expr):
 t=f['type']
 if t==9:return f'Text(writer, {expr});'
 if t==12:return f'Bytes(writer, {expr});'
 if t==11:return f'Write(writer, {expr});'
 if t==14:return f'writer.Write((uint){expr});'
 return f'writer.Write({expr});'
cs='''// Explicit authority field encoding. Generated from the canonical schema; no protobuf serialization/reflection at runtime.
using System;
using System.IO;
using System.Text;
using Google.Protobuf;
using Wire = XingyiStarry.Mp.Protocol.Wire;
namespace XingyiStarry.Mp.Protocol;
internal static class AuthorityFieldEncoding
{
    internal static byte[] Encode(AuthorityFrame frame)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        Text(writer, ProtocolConstants.HashDomain);
        Bytes(writer, ByteString.CopyFrom(frame.MatchId.ToByteArray()));
        writer.Write(frame.FrameId); Bytes(writer, ByteString.CopyFrom(frame.PrevHash));
        writer.Write((uint)(100 + (int)frame.FrameType));
        switch (frame.FrameType)
        {
'''
for n,t in maps['frames'].items():cs+=f'            case AuthorityFrameType.{n}: Write(writer, (Wire.{t})frame.Payload); break;\n'
cs+='''            default: throw new InvalidDataException("Unknown authority frame body.");
        }
        return stream.ToArray();
    }
    private static void Text(BinaryWriter writer, string value) { var data = Encoding.UTF8.GetBytes(value); writer.Write((uint)data.Length); writer.Write(data); }
    private static void Bytes(BinaryWriter writer, ByteString value) { writer.Write((uint)value.Length); writer.Write(value.ToByteArray()); }
'''
for name,(fields,groups) in messages.items():
 cs+=f'    private static void Write(BinaryWriter writer, Wire.{name} value)\n    {{\n'
 emitted=set()
 for f in fields:
  p=pas(f['name']);e='value.'+p;g=f['group']
  if g is not None and not f['optional']:
   if g in emitted:continue
   emitted.add(g);gp=pas(groups[g]);cs+=f'        writer.Write((uint)value.{gp}Case);\n        switch (value.{gp}Case)\n        {{\n'
   for x in fields:
    if x['group']==g:cs+=f'            case Wire.{name}.{gp}OneofCase.{pas(x["name"])}: {csval(x,"value."+pas(x["name"]))} break;\n'
   cs+='            default: throw new InvalidDataException("Missing oneof value.");\n        }\n'
  elif f['label']==3:
   cs+=f'        writer.Write((uint){e}.Count);\n        foreach (var item in {e}) {{ {csval(f,"item")} }}\n'
  elif f['type']==11 or f['optional']:
   condition=e+' is not null' if f['type']==11 else 'value.Has'+p
   cs+=f'        writer.Write({condition});\n        if ({condition}) {{ {csval(f,e)} }}\n'
  else:cs+='        '+csval(f,e)+'\n'
 cs+='    }\n'
cs+='}\n';(r/'src/XingyiStarry.Mp.Protocol/AuthorityFieldEncoding.cs').write_text(cs,encoding='utf-8')

print('Generated explicit hash writers for',len(messages),'messages')

def pas(n):return ''.join(x[:1].upper()+x[1:] for x in n.split('_'))
def write(f,e):
 t=f['type']
 if t==11:return f'hash{f["typename"]}(h, {e})'
 if t in [9,12]:return f'h.data([]byte({e}))' if t==9 else f'h.data({e})'
 if t==8:return f'h.flag({e})'
 if t==1:return f'h.u64(math.Float64bits({e}))'
 if t==2:return f'h.u32(math.Float32bits({e}))'
 if t in [3,4,6,16,18]:return f'h.u64(uint64({e}))'
 return f'h.u32(uint32({e}))'
s='''// Code generated from the canonical MP schema. DO NOT EDIT.
package wire

import (
 "bytes"
 "crypto/sha256"
 "encoding/binary"
 "errors"
 "math"
)

type fieldHasher struct { bytes.Buffer; err error }
func (h *fieldHasher) u32(v uint32) { var b [4]byte; binary.LittleEndian.PutUint32(b[:],v); h.Write(b[:]) }
func (h *fieldHasher) u64(v uint64) { var b [8]byte; binary.LittleEndian.PutUint64(b[:],v); h.Write(b[:]) }
func (h *fieldHasher) data(v []byte) { h.u32(uint32(len(v))); h.Write(v) }
func (h *fieldHasher) flag(v bool) { if v { h.WriteByte(1) } else { h.WriteByte(0) } }

// AuthorityFrameHash encodes explicit v21 fields, independently of protobuf serialization.
func AuthorityFrameHash(frame *AuthorityFrameMessage) ([]byte,error) {
 h:= &fieldHasher{}
 h.data([]byte("XingyiStarry.Mp/authority/v2"))
 h.data(frame.MatchId); h.u64(uint64(frame.FrameId)); h.data(frame.PreviousHash)
 switch body:=frame.Body.(type) {
'''
for n,v in maps['frame_ids']:s+=f' case *AuthorityFrameMessage_{n}: h.u32({100+v}); hash{maps["frames"][n]}(h, body.{n})\n'
s+=''' default: return nil,errors.New("missing authority body")
 }
 if h.err!=nil {return nil,h.err}; hash:=sha256.Sum256(h.Bytes());return hash[:],nil
}
'''
for name,(fields,groups) in messages.items():
 s+=f'func hash{name}(h *fieldHasher, value *{name}) {{\n if value==nil {{ h.err=errors.New("nil {name}");return }}\n'
 emitted=set()
 for f in fields:
  p=pas(f['name']);e='value.'+p;g=f['group']
  if g is not None and not f['optional']:
   if g in emitted:continue
   emitted.add(g);gp=pas(groups[g]);s+=f' switch body:=value.{gp}.(type) {{\n'
   for x in fields:
    if x['group']==g:s+=f' case *{name}_{pas(x["name"])}: h.u32({x["num"]}); {write(x,"body."+pas(x["name"]))}\n'
   s+=' default: h.err=errors.New("missing oneof")\n }\n'
  elif f['label']==3:s+=f' h.u32(uint32(len({e})))\n for _,item:=range {e} {{ {write(f,"item")} }}\n'
  elif f['type']==11 or f['optional']:
   value=e if f['type'] in [11,12] else '*'+e
   s+=f' h.flag({e}!=nil)\n if {e}!=nil {{ {write(f,value)} }}\n'
  else:s+=' '+write(f,e)+'\n'
 s+='}\n'
out=Path(sys.argv[1]).resolve()/'internal/wire/authority_hash.go' if len(sys.argv)>1 else r.parent/'xingyistarry-mp-relay/internal/wire/authority_hash.go';out.write_text(s,encoding='utf-8')
print('Generated Go field hash writers')

subprocess.run(['gofmt','-w',str(out)],check=True)
