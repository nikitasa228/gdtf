using System.Buffers.Binary;
using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Mvr.Tests;

/// <summary>
/// A hand-written MVR 1.6 archive that uses every object type and most attributes: nested groups, symbol definitions
/// (one nested in another), a GLB and a 3DS model, a mirror matrix, a layer with a matrix, connections, a complete
/// embedded GDTF, a stub GDTF, a GDTF only in the library and a model-less focus point geometry.
/// </summary>
internal static class SyntheticMvr
{
    public const string ClassLighting = "11111111-0000-4000-8000-000000000001";
    public const string PositionElectric = "11111111-0000-4000-8000-000000000002";
    public const string SymdefChair = "22222222-0000-4000-8000-000000000001";
    public const string SymdefRow = "22222222-0000-4000-8000-000000000002";
    public const string MappingDef = "33333333-0000-4000-8000-000000000001";
    public const string LayerRig = "44444444-0000-4000-8000-000000000001";
    public const string LayerSet = "44444444-0000-4000-8000-000000000002";
    public const string Focus = "55555555-0000-4000-8000-000000000001";
    public const string Truss = "66666666-0000-4000-8000-000000000001";
    public const string Spot = "66666666-0000-4000-8000-000000000002";
    public const string SafetyBox = "66666666-0000-4000-8000-000000000003";
    public const string Hoist = "66666666-0000-4000-8000-000000000004";
    public const string OuterGroup = "66666666-0000-4000-8000-000000000005";
    public const string InnerGroup = "66666666-0000-4000-8000-000000000006";
    public const string Wash = "66666666-0000-4000-8000-000000000007";
    public const string Chairs = "66666666-0000-4000-8000-000000000008";
    public const string Screen = "66666666-0000-4000-8000-000000000009";
    public const string Projector = "66666666-0000-4000-8000-00000000000A";
    public const string Mirrored = "66666666-0000-4000-8000-00000000000B";
    public const string StubFixture = "66666666-0000-4000-8000-00000000000C";
    public const string SetPiece = "66666666-0000-4000-8000-00000000000D";
    public const string ChairSymbol = "77777777-0000-4000-8000-000000000001";
    public const string RowSymbol = "77777777-0000-4000-8000-000000000002";
    public const string NestedSymbol = "77777777-0000-4000-8000-000000000003";

    /// <summary>Library file embedded (complete) under the name <see cref="EmbeddedSpec"/>.</summary>
    public const string EmbeddedLibraryFile = "Cyclops_Lighting@Sparkly_300B@Cyclops_Lighting_Official_Library.gdtf";

    public const string EmbeddedSpec = "Cyclops@Sparkly 300B.gdtf";
    public const string LibrarySpec = "Robe@Robin MegaPointe.gdtf";
    public const string StubSpec = "Ayrton@Zonda 3 FX.gdtf";

    /// <summary>The GLB box: 2 m (X) × 1 m (Y) × 0.5 m (Z), from the origin.</summary>
    public static readonly Vector3 BoxSize = new(2f, 1f, 0.5f);

    /// <summary>The 3DS truss: 3000 × 290 × 290 mm, from the origin.</summary>
    public static readonly Vector3 TrussSizeMm = new(3000f, 290f, 290f);

    /// <summary>The scene description; <paramref name="libraryMode"/> is a mode name of the MegaPointe library file.</summary>
    public static string Xml(string libraryMode) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <GeneralSceneDescription verMajor="1" verMinor="6" provider="TestSuite" providerVersion="2.1">
          <UserData>
            <Data provider="TestSuite" ver="1.0"><Setting name="grid" value="1"/></Data>
          </UserData>
          <Scene>
            <AUXData>
              <Class uuid="{{ClassLighting}}" name="Lighting"/>
              <Position uuid="{{PositionElectric}}" name="1st Electric"/>
              <Symdef uuid="{{SymdefChair}}" name="Chair">
                <ChildList>
                  <Geometry3D fileName="box.glb"/>
                  <Geometry3D fileName="box.glb"><Matrix>{1,0,0}{0,1,0}{0,0,1}{0,0,1000}</Matrix></Geometry3D>
                </ChildList>
              </Symdef>
              <Symdef uuid="{{SymdefRow}}" name="Row">
                <ChildList>
                  <Symbol uuid="{{NestedSymbol}}" symdef="{{SymdefChair}}"><Matrix>{1,0,0}{0,1,0}{0,0,1}{5000,0,0}</Matrix></Symbol>
                </ChildList>
              </Symdef>
              <MappingDefinition uuid="{{MappingDef}}" name="Screen map">
                <SizeX>1920</SizeX>
                <SizeY>1080</SizeY>
                <Source linkedGeometry="Display" type="File">movie.mp4</Source>
                <ScaleHandeling>ScaleKeepRatio</ScaleHandeling>
              </MappingDefinition>
            </AUXData>
            <Layers>
              <Layer uuid="{{LayerRig}}" name="Rig">
                <ChildList>
                  <FocusPoint uuid="{{Focus}}" name="Centre">
                    <Matrix>{1,0,0}{0,1,0}{0,0,1}{1000,2000,0}</Matrix>
                    <Classing>{{ClassLighting}}</Classing>
                    <Geometries><Geometry3D fileName="box.glb"/></Geometries>
                  </FocusPoint>
                  <Truss uuid="{{Truss}}" name="Truss 1">
                    <Matrix>{1,0,0}{0,1,0}{0,0,1}{0,5000,6000}</Matrix>
                    <Classing>{{ClassLighting}}</Classing>
                    <Position>{{PositionElectric}}</Position>
                    <Geometries><Geometry3D fileName="truss.3ds"/></Geometries>
                    <Function>Front truss</Function>
                    <ChildList>
                      <Fixture uuid="{{Spot}}" name="Spot 1">
                        <Matrix>{1,0,0}{0,1,0}{0,0,1}{1000,0,-500}</Matrix>
                        <Classing>{{ClassLighting}}</Classing>
                        <GDTFSpec>{{LibrarySpec}}</GDTFSpec>
                        <GDTFMode>{{libraryMode}}</GDTFMode>
                        <Focus>{{Focus}}</Focus>
                        <CastShadow>true</CastShadow>
                        <DMXInvertPan>true</DMXInvertPan>
                        <DMXInvertTilt>false</DMXInvertTilt>
                        <Position>{{PositionElectric}}</Position>
                        <Function>Key light</Function>
                        <FixtureID>101</FixtureID>
                        <FixtureIDNumeric>101</FixtureIDNumeric>
                        <UnitNumber>7</UnitNumber>
                        <ChildPosition>Base.Yoke</ChildPosition>
                        <Addresses>
                          <Address break="0">1.001</Address>
                          <Address break="1">1025</Address>
                          <Network geometry="NetPort" ipv4="10.0.0.5" subnetmask="255.0.0.0" dhcp="off" hostname="spot1"/>
                        </Addresses>
                        <Protocols><Protocol geometry="NetPort" name="Main" type="sACN" version="2016" transmission="Multicast"/></Protocols>
                        <Alignments><Alignment geometry="Beam" up="0,1,0" direction="0,0,-1"/></Alignments>
                        <CustomCommands><CustomCommand>Body_Pan,f 50</CustomCommand></CustomCommands>
                        <Overwrites><Overwrite universal="Universal/Gobo1" target="Gobo1"/></Overwrites>
                        <Connections><Connection own="Input" other="Output" toObject="{{Wash}}"/></Connections>
                        <Color>0.3127,0.329,100</Color>
                        <CustomIdType>1</CustomIdType>
                        <CustomId>42</CustomId>
                        <Mappings><Mapping linkedDef="{{MappingDef}}"><ux>10</ux><uy>20</uy><ox>1</ox><oy>2</oy><rz>45</rz></Mapping></Mappings>
                        <Gobo rotation="32.5">gobo.png</Gobo>
                        <ChildList>
                          <SceneObject uuid="{{SafetyBox}}" name="Safety box">
                            <Geometries><Geometry3D fileName="box.glb"/></Geometries>
                          </SceneObject>
                        </ChildList>
                      </Fixture>
                    </ChildList>
                  </Truss>
                  <Support uuid="{{Hoist}}" name="Hoist A">
                    <Matrix>{1,0,0}{0,1,0}{0,0,1}{0,5000,8000}</Matrix>
                    <Geometries><Geometry3D fileName="box.glb"/></Geometries>
                    <Function>Chain hoist</Function>
                    <ChainLength>2000</ChainLength>
                  </Support>
                  <GroupObject uuid="{{OuterGroup}}" name="Outer group">
                    <Matrix>{1,0,0}{0,1,0}{0,0,1}{0,0,1000}</Matrix>
                    <ChildList>
                      <GroupObject uuid="{{InnerGroup}}" name="Inner group">
                        <Matrix>{0,1,0}{-1,0,0}{0,0,1}{2000,0,0}</Matrix>
                        <ChildList>
                          <Fixture uuid="{{Wash}}" name="Wash 1" multipatch="{{Spot}}">
                            <Matrix>{1,0,0}{0,1,0}{0,0,1}{1000,0,0}</Matrix>
                            <GDTFSpec>{{EmbeddedSpec}}</GDTFSpec>
                            <GDTFMode>16 CH</GDTFMode>
                            <FixtureID>A-7</FixtureID>
                            <FixtureIDNumeric>7</FixtureIDNumeric>
                            <Addresses><Address break="0">513</Address></Addresses>
                          </Fixture>
                          <SceneObject uuid="{{Chairs}}" name="Chairs">
                            <Geometries>
                              <Symbol uuid="{{ChairSymbol}}" symdef="{{SymdefChair}}"><Matrix>{1,0,0}{0,1,0}{0,0,1}{0,3000,0}</Matrix></Symbol>
                              <Symbol uuid="{{RowSymbol}}" symdef="{{SymdefRow}}"/>
                            </Geometries>
                          </SceneObject>
                        </ChildList>
                      </GroupObject>
                    </ChildList>
                  </GroupObject>
                  <VideoScreen uuid="{{Screen}}" name="LED wall">
                    <Matrix>{1,0,0}{0,0,1}{0,-1,0}{0,10000,0}</Matrix>
                    <Geometries><Geometry3D fileName="box.glb"/></Geometries>
                    <Sources><Source linkedGeometry="Display" type="NDI">Stream 1</Source></Sources>
                    <Function>IMAG</Function>
                  </VideoScreen>
                  <Projector uuid="{{Projector}}" name="Projector">
                    <Geometries><Geometry3D fileName="box.glb"/></Geometries>
                    <Projections>
                      <Projection>
                        <Source linkedGeometry="Lens" type="File">slide.png</Source>
                        <ScaleHandeling>ScaleIgnoreRatio</ScaleHandeling>
                      </Projection>
                    </Projections>
                  </Projector>
                  <SceneObject uuid="{{Mirrored}}" name="Mirrored chair">
                    <Matrix>{-1,0,0}{0,1,0}{0,0,1}{3000,0,0}</Matrix>
                    <Geometries><Geometry3D fileName="box.glb"/></Geometries>
                  </SceneObject>
                  <Fixture uuid="{{StubFixture}}" name="Stub fixture">
                    <GDTFSpec>{{StubSpec}}</GDTFSpec>
                    <GDTFMode>Basic</GDTFMode>
                    <Addresses><Address>100</Address></Addresses>
                    <FixtureID>9</FixtureID>
                  </Fixture>
                </ChildList>
              </Layer>
              <Layer uuid="{{LayerSet}}" name="Set">
                <Matrix>{1,0,0}{0,1,0}{0,0,1}{0,0,500}</Matrix>
                <ChildList>
                  <SceneObject uuid="{{SetPiece}}" name="Set piece">
                    <Matrix>{1,0,0}{0,1,0}{0,0,1}{1000,0,0}</Matrix>
                    <Geometries><Geometry3D fileName="box.glb"/></Geometries>
                  </SceneObject>
                </ChildList>
              </Layer>
            </Layers>
          </Scene>
        </GeneralSceneDescription>
        """;

    /// <summary>The archive bytes: the XML above plus box.glb, truss.3ds, gobo.png and the GDTF files.</summary>
    public static byte[] Build(string? xml = null, params (string Name, byte[] Data)[] extra)
    {
        var entries = new List<(string, byte[])>
        {
            ("GeneralSceneDescription.xml", TestData.Utf8(xml ?? Xml(LibraryMode()))),
            ("box.glb", BoxGlb()),
            ("truss.3ds", TrussThreeDs()),
            ("gobo.png", new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }),
            (EmbeddedSpec, File.ReadAllBytes(Path.Combine(TestData.RepoRoot, EmbeddedLibraryFile))),
            (StubSpec, StubGdtf()),
        };
        entries.AddRange(extra);
        return TestData.Zip(entries.ToArray());
    }

    /// <summary>The first DMX mode of the MegaPointe library file.</summary>
    public static string LibraryMode()
    {
        var match = TestData.Library.FindBySpec(LibrarySpec)!;
        return TestData.Library.GetFixtureType(match.Entry).DmxModes[0].Name;
    }

    /// <summary>The Depence stub of the Zonda 3 FX taken from <c>stages/Фокина.mvr</c>.</summary>
    public static byte[] StubGdtf() => MvrReader.ReadFile(TestData.FokinaMvr).FindFile(StubSpec)!.Bytes!;

    public static byte[] BoxGlb()
    {
        var model = new ModelData { Name = "Box" };
        model.Parts.Add(new ModelPart { Name = "Box", Mesh = SceneExporter.Box(Vector3.Zero, BoxSize) });
        model.Materials.Add(new MaterialData { Name = "Grey", BaseColorFactor = new Vector4(0.5f, 0.5f, 0.5f, 1f) });
        return GlbWriter.Write(model);
    }

    /// <summary>A minimal 3DS file (main → editor → object → tri-mesh with vertices and faces) of a box in millimetres.</summary>
    public static byte[] TrussThreeDs()
    {
        var box = SceneExporter.Box(Vector3.Zero, TrussSizeMm);
        var vertices = new byte[2 + box.Positions.Length * 12];
        BinaryPrimitives.WriteUInt16LittleEndian(vertices, (ushort)box.Positions.Length);
        for (int i = 0; i < box.Positions.Length; i++)
        {
            var p = box.Positions[i];
            BinaryPrimitives.WriteSingleLittleEndian(vertices.AsSpan(2 + i * 12), p.X);
            BinaryPrimitives.WriteSingleLittleEndian(vertices.AsSpan(6 + i * 12), p.Y);
            BinaryPrimitives.WriteSingleLittleEndian(vertices.AsSpan(10 + i * 12), p.Z);
        }
        int faceCount = box.Indices.Length / 3;
        var faces = new byte[2 + faceCount * 8];
        BinaryPrimitives.WriteUInt16LittleEndian(faces, (ushort)faceCount);
        for (int f = 0; f < faceCount; f++)
        {
            for (int k = 0; k < 3; k++)
                BinaryPrimitives.WriteUInt16LittleEndian(faces.AsSpan(2 + f * 8 + k * 2), (ushort)box.Indices[f * 3 + k]);
            BinaryPrimitives.WriteUInt16LittleEndian(faces.AsSpan(2 + f * 8 + 6), 7);
        }
        var mesh = Chunk(0x4100, Chunk(0x4110, vertices), Chunk(0x4120, faces));
        var obj = Chunk(0x4000, TestData.Utf8("Truss\0"), mesh);
        return Chunk(0x4D4D, Chunk(0x3D3D, obj));
    }

    private static byte[] Chunk(ushort id, params byte[][] parts)
    {
        int length = 6 + parts.Sum(p => p.Length);
        var data = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(data, id);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2), (uint)length);
        int at = 6;
        foreach (var p in parts)
        {
            p.CopyTo(data, at);
            at += p.Length;
        }
        return data;
    }
}
