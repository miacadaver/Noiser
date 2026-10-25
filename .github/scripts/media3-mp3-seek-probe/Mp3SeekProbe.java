import androidx.media3.common.*;
import androidx.media3.common.util.*;
import androidx.media3.extractor.*;
import androidx.media3.extractor.mp3.*;
import java.io.*;
import java.util.*;

public final class Mp3SeekProbe {
  static class Frontier extends IOException {}
  static class Reader implements DataReader {
    final byte[] data; int pos; final int limit;
    Reader(byte[] data, int pos, int limit) { this.data=data;this.pos=pos;this.limit=limit; }
    public int read(byte[] b,int o,int n) throws IOException {
      if(pos>=limit) { if(limit<data.length) throw new Frontier(); return -1; }
      int count=Math.min(n,limit-pos);System.arraycopy(data,pos,b,o,count);pos+=count;return count;
    }
  }
  static class Output implements ExtractorOutput,TrackOutput {
    SeekMap map; long lastTime=-1; int samples=0;
    public TrackOutput track(int id,int type) { return this; }
    public void endTracks() {}
    public void seekMap(SeekMap value) { map=value; }
    public void format(Format f) {}
    public int sampleData(DataReader in,int len,boolean eof,int part) throws IOException {
      return in.read(new byte[len],0,len);
    }
    public void sampleData(ParsableByteArray b,int n,int part) { b.skipBytes(n); }
    public void sampleMetadata(long t,int flags,int size,int offset,TrackOutput.CryptoData c) {
      lastTime=t;samples++;
    }
  }
  static byte[] fixture(boolean vbr,int id3Bytes) throws Exception {
    ByteArrayOutputStream b=new ByteArrayOutputStream();
    if(id3Bytes>0) {
      b.write(new byte[]{'I','D','3',4,0,0,(byte)((id3Bytes>>21)&127),(byte)((id3Bytes>>14)&127),(byte)((id3Bytes>>7)&127),(byte)(id3Bytes&127)});
      b.write(new byte[id3Bytes]);
    }
    for(int i=0;i<200;i++) {
      int bitrate=vbr?(i%2==0?1:14):9;
      int h=0xfffb0000|(bitrate<<12);
      int size=MpegAudioUtil.getFrameSize(h);
      byte[] frame=new byte[size];frame[0]=(byte)(h>>>24);frame[1]=(byte)(h>>>16);frame[2]=(byte)(h>>>8);frame[3]=(byte)h;
      b.write(frame);
    }
    return b.toByteArray();
  }
  static ExtractorInput input(byte[] data,int pos,int limit,boolean mask) {
    ExtractorInput in=new DefaultExtractorInput(new Reader(data,pos,limit),pos,data.length);
    return mask?new ForwardingExtractorInput(in){public long getLength(){return C.LENGTH_UNSET;}}:in;
  }
  static void consume(Mp3Extractor e,ExtractorInput in) throws Exception {
    PositionHolder p=new PositionHolder();
    for(int i=0;i<10000;i++) {if(e.read(in,p)==Extractor.RESULT_END_OF_INPUT)return;}
    throw new AssertionError("Read loop did not terminate");
  }
  static void run(boolean vbr,int id3Bytes,boolean mask) throws Exception {
    byte[] data=fixture(vbr,id3Bytes);
    int audioStart=id3Bytes==0?0:id3Bytes+10;
    int limit=audioStart+(data.length-audioStart)/3+21;
    Mp3Extractor e=new Mp3Extractor(Mp3Extractor.FLAG_ENABLE_INDEX_SEEKING);
    Output out=new Output();e.init(out);
    try {consume(e,input(data,0,limit,mask));throw new AssertionError("Expected received frontier");}
    catch(Frontier expected){}
    String kind=out.map.getClass().getSimpleName();
    if(mask&&!kind.equals("IndexSeeker"))throw new AssertionError(kind);
    long safe=out.lastTime-500000;
    SeekPoint point=out.map.getSeekPoints(safe).first;
    if(point.position>=limit)throw new AssertionError("Seek outside prefix");
    int before=out.samples;
    e.seek(point.position,point.timeUs);
    try {consume(e,input(data,(int)point.position,limit,mask));}catch(Frontier expected){}
    if(out.samples<=before)throw new AssertionError("No samples after seek");
    e.seek(0,0);consume(e,input(data,0,data.length,mask));
    if(mask&&(out.map.getDurationUs()<5000000||out.map.getDurationUs()>5300000))throw new AssertionError("Final duration "+out.map.getDurationUs());
    System.out.printf("PASS %s, ID3=%d, mask=%s: %s; partial seek at %d/%d bytes; full duration=%dus%n",vbr?"VBR":"CBR",id3Bytes,mask,kind,point.position,limit,out.map.getDurationUs());
  }
  public static void main(String[] args) throws Exception {
    run(false,0,true);run(true,0,true);run(true,200000,true);run(true,0,false);
  }
}
