namespace MinecraftCS;

internal static unsafe partial class Program
{
    // Static contact lighting belongs to geometry, not to every screen pixel.
    static byte VertexAO(int x, int y, int z, int face, int corner)
    {
        int nx=FaceDir[face,0], ny=FaceDir[face,1], nz=FaceDir[face,2];
        int axisA = nx == 0 ? 0 : 1;
        int axisB = nz == 0 ? 2 : 1;
        int a = FaceVerts[face,corner,axisA] == 0 ? -1 : 1;
        int b = FaceVerts[face,corner,axisB] == 0 ? -1 : 1;
        int ax=axisA==0?a:0, ay=axisA==1?a:0;
        int by=axisB==1?b:0, bz=axisB==2?b:0;
        x+=nx; y+=ny; z+=nz;
        int sideA=Get(x+ax,y+ay,z)!=Air?1:0;
        int sideB=Get(x,y+by,z+bz)!=Air?1:0;
        int diagonal=Get(x+ax,y+ay+by,z+bz)!=Air?1:0;
        float ao=sideA*sideB==1?0.48f:1f-(sideA+sideB+diagonal)*0.16f;
        return (byte)MathF.Round(255*ao);
    }
}
