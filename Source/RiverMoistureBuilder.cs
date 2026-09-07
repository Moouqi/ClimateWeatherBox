using System;

namespace ClimateWeather;

// Incremental chamfer field with a periodic halo as wide as the influence radius.
// Two bounded passes are sufficient even across the seam: every relevant image
// of a source exists in the padded rectangle. North/south are never wrapped.
internal sealed class RiverMoistureBuilder
{
    internal readonly int Width, Height, Radius;
    internal readonly bool Wrap;
    private readonly int _halo, _stride, _total;
    private readonly float[] _distance;
    private float[] _result;
    private int _phase = 4, _cursor;
    internal bool Complete => _phase == 4;
    internal float[] Result => _result;

    internal RiverMoistureBuilder(int width,int height,int radius,bool wrap)
    {
        if (width<=0 || height<=0 || radius<0) throw new ArgumentOutOfRangeException();
        Width=width; Height=height; Radius=radius; Wrap=wrap;
        _halo=wrap ? radius : 0; _stride=checked(width+2*_halo);
        _total=checked(_stride*height);
        _distance=new float[_total]; _result=new float[checked(width*height)];
    }
    internal void Begin() { _phase=0; _cursor=0; }
    internal void RecycleOutput(float[] old)
    {
        if (!Complete) throw new InvalidOperationException();
        _result=old.Length==Width*Height ? old : new float[Width*Height];
    }
    internal void Step(int budget,Func<int,int,bool> source)
    {
        const float diagonal=1.41421356f;
        while (budget-- > 0 && !Complete)
        {
            int p = _phase==2 ? _total-1-_cursor : _cursor;
            if (_phase==0)
            {
                int x=p%_stride-_halo, y=p/_stride;
                if (Wrap) x=HorizontalTopology.Wrap(x,Width);
                _distance[p]=source(x,y) ? 0f : 100000f;
            }
            else if (_phase==1 || _phase==2)
            {
                int x=p%_stride,y=p/_stride;
                float best=_distance[p];
                if (_phase==1)
                {
                    if (x>0) best=Math.Min(best,_distance[p-1]+1);
                    if (y>0)
                    {
                        best=Math.Min(best,_distance[p-_stride]+1);
                        if (x>0) best=Math.Min(best,_distance[p-_stride-1]+diagonal);
                        if (x+1<_stride) best=Math.Min(best,_distance[p-_stride+1]+diagonal);
                    }
                }
                else
                {
                    if (x+1<_stride) best=Math.Min(best,_distance[p+1]+1);
                    if (y+1<Height)
                    {
                        best=Math.Min(best,_distance[p+_stride]+1);
                        if (x>0) best=Math.Min(best,_distance[p+_stride-1]+diagonal);
                        if (x+1<_stride) best=Math.Min(best,_distance[p+_stride+1]+diagonal);
                    }
                }
                _distance[p]=best;
            }
            else
            {
                float d=_distance[(_cursor/Width)*_stride+_cursor%Width+_halo];
                _result[_cursor]=d<=Radius ? (1f-d/(Radius+.5f))*.34f : 0f;
            }
            if (++_cursor >= (_phase==3 ? Width*Height : _total)) { _phase++; _cursor=0; }
        }
    }
}
