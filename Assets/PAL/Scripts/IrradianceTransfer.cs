﻿//
// POLYGONAL AREA LIGHTS
// The MIT License (MIT)
// Copyright (c) 2016 ALEXANDER PETRYAEV
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal 
// in the Software without restriction, including without limitation the rights 
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell 
// copies of the Software, and to permit persons to whom the Software is furnished 
// to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, 
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A 
// PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT 
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION 
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE 
// SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public enum IrradianceMapResolution
{
	_16x16,
	_32x32,
	_64x64,
	_96x96,
	_128x128,
	_192x192,
	_256x256
};

static public class AngularMeter
{	
	public static float GetAngle(this Vector3 thisVector, Vector3 otherVector)
	{
		return GetAngle( thisVector, otherVector, Vector3.Cross( thisVector, otherVector ).normalized );
	}

	public static float GetAngle(this Vector3 thisVector, Vector3 otherVector, Vector3 axisVector)
	{
		float angle = Vector3.Dot( thisVector, otherVector );
		angle = ( angle > 1 ) ? ( 0 ) : ( ( angle < -1 ) ? ( Mathf.Rad2Deg * Mathf.PI ) : ( angle = Mathf.Rad2Deg * Mathf.Acos( angle ) ) );
		Vector3 crossTestAxis = Vector3.Cross( thisVector, otherVector );
		if( !Mathf.Approximately( crossTestAxis.sqrMagnitude, 0.0f ) )
		{
			angle = angle * Mathf.Sign( Vector3.Dot( crossTestAxis, axisVector ) );
		}
		return angle;
	}
}

[RequireComponent(typeof(MeshAreaLight))]
public partial class IrradianceTransfer : MonoBehaviour
{
	[Header("Watch buffers")]
	public RenderTexture AlbedoBuffer;
	public RenderTexture DepthBuffer;
	public RenderTexture NormalBuffer;
	public RenderTexture MergeBuffer;
	public RenderTexture GeometryBuffer;
	public RenderTexture IlluminationBuffer;

	#region Constants
	// algorithm constants
	const float IlluminationBufferIntensityScale = 0.125f;
	const int   IrradiancePolygonSmoothing = 4;
	const float OutlineOffset = 0.25f;

	// RGBA-to-float decoding constants (from UnityCG.cginc) for Color32 values
	const float kDecodeRedFactor = (1/255.0f);
	const float kDecodeGreenFactor = (1/255.0f)/255f; 
	const float kDecodeBlueFactor = (1/65025.0f)/255f;
	const float kDecodeAlphaFactor = (1/16581375.0f)/255f;
	#endregion

	#region EmbeddedTypes
	public struct PixelCoords
	{
		public int x;
		public int y;

		public PixelCoords(int x, int y)
		{
			this.x = x;
			this.y = y;
		}

		public static PixelCoords zero = new PixelCoords( 0,0 );
	};

	public class IrradiancePolygon : PolygonalAreaLight
	{
		public int       polygonIndex = 0;
		public int       totalPixels = 0;
		public float     totalIllumination = 0.0f;
		public int       totalRed = 0;
		public int       totalGreen = 0;
		public int       totalBlue = 0;
		public Plane     polygonPlane = new Plane();
		public Vector3[] smoothVertices = new Vector3[0];
		public bool[]    vertexFlags = new bool[0];
	};
	#endregion

	#region PublicFields
	[Header("Settings")]
	public IrradianceMapResolution Resolution = IrradianceMapResolution._32x32;

	[Range(0.0f, 2.0f)]
	public float BounceIntensityTreshold = 0.5f;

	[Range(0.0f, 2.0f)]
	public float IrradianceBias = 0.0f;

	[Range(0.0f, 5.0f)]
	public float IrradianceIntensityMultiplier = 1.0f;

	[Range(90.0f, 165.0f)]
	public float OffscreenCameraFOV = 145.0f;
	#endregion

	#region PrivateFields
	Transform             _thisTransform = null;
	Vector3               _prevTransformFingerprint = new Vector3( float.MaxValue, float.MaxValue, float.MaxValue );
	float                 _prevIntensity = float.MaxValue;
	Vector2               _irradianceMapBufferResolution = Vector2.zero;
	Vector2               _irradianceMapInvBufferResolution = Vector2.zero;
	Vector4               _irradianceMapPixelSize = Vector4.zero;
	MeshAreaLight         _meshAreaLight;
	Shader                _albedoBufferShader;
	Shader                _depthBufferShader;
	Shader                _normalBufferShader;
	Shader                _geometryBufferShader;
	Shader                _mergeBufferShader;
	Shader                _illuminationBufferShader;
	Material              _mergeBufferMaterial;
	Camera                _offscreenCamera;
	RenderTexture         _albedoBuffer;
	RenderTexture         _depthBuffer;
	RenderTexture         _normalBuffer;
	RenderTexture         _mergeBuffer;
	RenderTexture         _geometryBuffer;
	RenderTexture         _illuminationBuffer;
	Texture2D             _transferBuffer;
	Color32[]             _albedoBufferPixels = new Color32[0];
	Color32[]             _depthBufferPixels = new Color32[0];
	Color32[]             _mergeBufferPixels = new Color32[0];
	Color32[]             _illuminationBufferPixels = new Color32[0];
	int                   _numPolygons = 0;
	int[]                 _polygonMap = new int[0];
	int[]                 _polygonMergeMap = new int[0];
	int[]                 _polygonSize = new int[0];
	bool[]                _thresholdMap = new bool[0];
	ushort[]              _contourMap = new ushort[0];
	IrradiancePolygon[]   _irradiancePolygons = new IrradiancePolygon[0];
	#endregion

	#region MarchingSquares
	#endregion

	#region MonoBehaviour
	void Awake()
	{
		_thisTransform = this.transform;

		string[] s = Resolution.ToString().Trim( '_' ).Split( 'x' );

		int width = 0;
		int height = 0;

		if( int.TryParse( s[0], out width ) && int.TryParse( s[1], out height ) )
		{
			_irradianceMapBufferResolution.Set( (float)(width), (float)(height) );
			_irradianceMapInvBufferResolution.Set( 1.0f/width, 1.0f/height );
			_irradianceMapPixelSize.Set( 1.0f/width, 1.0f/height, 0.0f, 0.0f );
		}
		else
		{
			Debug.LogError( "[IrradianceTransfer] Awake() unable to parse buffer resolution from enum value " + Resolution.ToString() + ", forced to 16x16" );
			_irradianceMapBufferResolution.Set( 16.0f, 16.0f );
			_irradianceMapInvBufferResolution.Set( 1.0f/16.0f, 1.0f/16.0f );
			_irradianceMapPixelSize.Set( 1.0f/16.0f, 1.0f/16.0f, 0.0f, 0.0f );
		}
	}

	void OnDestroy()
	{
		for( int i=0; i<_irradiancePolygons.Length; i++ )
		{
			if( _irradiancePolygons[i] != null )
			{
				PALBatchBuilder.UnregisterPolygonalAreaLight( _irradiancePolygons[i] );
			}
		}
	}

	void Start()
	{
		_meshAreaLight = GetComponent<MeshAreaLight>();
		Bounds polygonBounds = _meshAreaLight.PolygonBounds;
		Vector3 x = _meshAreaLight.transform.localToWorldMatrix.MultiplyVector( Vector3.right ) * polygonBounds.extents.x;
		Vector3 y = _meshAreaLight.transform.localToWorldMatrix.MultiplyVector( Vector3.up ) * polygonBounds.extents.y;
		Vector3 z = _meshAreaLight.transform.localToWorldMatrix.MultiplyVector( Vector3.forward ) * polygonBounds.extents.z;

		GameObject offscreenCameraObject = new GameObject("OffscreenCamera");
		offscreenCameraObject.hideFlags = HideFlags.HideAndDontSave;// HideFlags.DontSave; // 
		offscreenCameraObject.transform.parent = this.transform;
		offscreenCameraObject.transform.localPosition = Vector3.zero;
		offscreenCameraObject.transform.localRotation = Quaternion.identity;
		float angle = offscreenCameraObject.transform.forward.GetAngle( _meshAreaLight.PolygonNormal );
		offscreenCameraObject.transform.Rotate( Vector3.Cross( offscreenCameraObject.transform.forward, _meshAreaLight.PolygonNormal ), angle, Space.World );

		_offscreenCamera = offscreenCameraObject.AddComponent<Camera>();

		_albedoBufferShader = Shader.Find( "PAL/Opaque" );
		_depthBufferShader = Shader.Find( "PAL/DepthBuffer" );
		_normalBufferShader = Shader.Find( "PAL/NormalBuffer" );
		_mergeBufferShader = Shader.Find( "PAL/MergeBuffer" );
		_illuminationBufferShader = Shader.Find( "PAL/IlluminationBuffer" );
		_geometryBufferShader = Shader.Find( "PAL/GeometryBuffer" );
		_mergeBufferMaterial = new Material( _mergeBufferShader );

		int bufferWidth = (int)(_irradianceMapBufferResolution.x);
		int bufferHeight = (int)(_irradianceMapBufferResolution.x);

		_albedoBuffer = new RenderTexture( bufferWidth, bufferHeight, 32, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear );
		_depthBuffer = new RenderTexture( bufferWidth, bufferHeight, 32, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear );
		_normalBuffer = new RenderTexture( bufferWidth, bufferHeight, 32, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear );
		_mergeBuffer = new RenderTexture( bufferWidth, bufferHeight, 32, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear );
		_geometryBuffer = new RenderTexture( bufferWidth, bufferHeight, 32, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear );
		_illuminationBuffer = new RenderTexture( bufferWidth, bufferHeight, 32, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear );
		_transferBuffer = new Texture2D( bufferWidth, bufferHeight, TextureFormat.ARGB32, false );

		_albedoBuffer.filterMode = FilterMode.Point;
		_depthBuffer.filterMode = FilterMode.Point;
		_normalBuffer.filterMode = FilterMode.Point;
		_mergeBuffer.filterMode = FilterMode.Point;
		_geometryBuffer.filterMode = FilterMode.Point;
		_illuminationBuffer.filterMode = FilterMode.Point;

		_offscreenCamera.nearClipPlane = 0.1f;
		_offscreenCamera.farClipPlane = 50.0f;
		_offscreenCamera.fieldOfView = OffscreenCameraFOV;
		_offscreenCamera.enabled = false;

		_polygonMap = new int[bufferWidth*bufferHeight];
		_polygonMergeMap = new int[bufferWidth*bufferHeight];
		_polygonSize = new int[bufferWidth*bufferHeight];
		_thresholdMap = new bool[bufferWidth*bufferHeight];
		_contourMap = new ushort[(bufferWidth-1)*(bufferHeight-1)];

		AlbedoBuffer = _albedoBuffer;
		DepthBuffer = _depthBuffer;
		NormalBuffer = _normalBuffer;
		MergeBuffer = _mergeBuffer;
		GeometryBuffer = _geometryBuffer;
		IlluminationBuffer = _illuminationBuffer;
	}

	void Update()
	{	
		Vector3 transformFingerPrint = _thisTransform.localToWorldMatrix.MultiplyPoint( Vector3.one );
		bool transformChanged = Vector3.Distance( transformFingerPrint, _prevTransformFingerprint ) > Mathf.Epsilon;
		transformChanged = transformChanged || ( _offscreenCamera.fieldOfView != OffscreenCameraFOV );
		bool intensityChanged = Mathf.Abs( _meshAreaLight.Intensity - _prevIntensity ) > Mathf.Epsilon;

		#if true
		transformChanged = true;
		intensityChanged = true;
		#endif

		if( !transformChanged && !intensityChanged )
		{
			for( int polygonIndex=0; polygonIndex<_irradiancePolygons.Length; polygonIndex++ )
			{
				if( _irradiancePolygons[polygonIndex] != null )
				{
					PALBatchBuilder.Update( _irradiancePolygons[polygonIndex] );
				}
			}
			return;
		}

		_prevTransformFingerprint = transformFingerPrint;
		_prevIntensity = _meshAreaLight.Intensity;

		for( int i=0; i<_irradiancePolygons.Length; i++ )
		{
			if( _irradiancePolygons[i] != null )
			{
				PALBatchBuilder.UnregisterPolygonalAreaLight( _irradiancePolygons[i] );
			}
			_irradiancePolygons[i] = null;
		}

		RenderBuffers( transformChanged, intensityChanged );
		BuildPolygonMap();

		int bufferHeight = _depthBuffer.height;
		int bufferWidth = _depthBuffer.width;

		PixelCoords marchingSquaresInf = new PixelCoords( bufferWidth-1, bufferHeight-1 );
		PixelCoords marchingSquaresSup = new PixelCoords( 0, 0 );

		CreateSecondaryAreaLights( ref marchingSquaresInf, ref marchingSquaresSup );

		EmbarassedMarchingSquares( marchingSquaresInf, marchingSquaresSup );

		Color averageAlbedo = Color.black;
		for( int polygonIndex=0; polygonIndex<_irradiancePolygons.Length; polygonIndex++ )
		{
			var irradiancePolygon = _irradiancePolygons[polygonIndex];
			if( irradiancePolygon == null ) continue;

			SmoothPolygon( irradiancePolygon );
			int numVertices = ReduceSemiParallelEdges( irradiancePolygon );

#if false
			CompressArrays( irradiancePolygon, numVertices );
#else
			CombineVertices( irradiancePolygon, numVertices );
#endif

			for( int index=0; index<irradiancePolygon.Vertices.Length; index++ )
			{
				irradiancePolygon.Vertices[index] += irradiancePolygon.polygonPlane.normal * 0.001f;
			}

			averageAlbedo.r = ( irradiancePolygon.totalRed / irradiancePolygon.totalPixels ) / 255.0f;
			averageAlbedo.g = ( irradiancePolygon.totalGreen / irradiancePolygon.totalPixels ) / 255.0f;
			averageAlbedo.b = ( irradiancePolygon.totalBlue / irradiancePolygon.totalPixels ) / 255.0f;

			irradiancePolygon.Color = _meshAreaLight.Color * averageAlbedo;
			irradiancePolygon.Bias = IrradianceBias;
			irradiancePolygon.Intensity = irradiancePolygon.totalIllumination / irradiancePolygon.totalPixels * IrradianceIntensityMultiplier;
			irradiancePolygon.Normal = irradiancePolygon.polygonPlane.normal;
			irradiancePolygon.ProjectionMode =_meshAreaLight.ProjectionMode;

			irradiancePolygon.Centroid = Vector3.zero;
			for( int index=0; index<irradiancePolygon.Vertices.Length; index++ )
			{
				irradiancePolygon.Centroid += irradiancePolygon.Vertices[index];
			}
			irradiancePolygon.Centroid *= 1.0f / irradiancePolygon.Vertices.Length;

			float circumradius = 0;
			for( int index=0; index<irradiancePolygon.Vertices.Length; index++ )
			{
				float distance = Vector3.Distance( irradiancePolygon.Centroid, irradiancePolygon.Vertices[index] );
				circumradius = Mathf.Max( circumradius, distance );
			}
			irradiancePolygon.Circumcircle.Set( irradiancePolygon.Centroid.x, irradiancePolygon.Centroid.y, irradiancePolygon.Centroid.z, circumradius );

			PALBatchBuilder.RegisterPolygonalAreaLight( irradiancePolygon );
			PALBatchBuilder.Update( irradiancePolygon );
		}
	}

	static Color[] gizmoColors = new Color[]
	{
		Color.red,
		Color.green,
		Color.blue,
		Color.yellow,
		Color.cyan,
		Color.magenta
	};

	void OnDrawGizmos()
	{
		for( int polygonIndex=0; polygonIndex<_irradiancePolygons.Length; polygonIndex++ )
		{
			var irradiancePolygon = _irradiancePolygons[polygonIndex];
			if( irradiancePolygon != null )
			{				
				if( irradiancePolygon.Vertices.Length > 1 )
				{
					Gizmos.color = gizmoColors[polygonIndex%gizmoColors.Length];
					for( int i=1; i<irradiancePolygon.Vertices.Length; i++ )
					{					
						Gizmos.DrawLine( irradiancePolygon.Vertices[i-1], irradiancePolygon.Vertices[i] );
						Gizmos.DrawCube( irradiancePolygon.Vertices[i-1], Vector3.one * 0.01f );
					}
					Gizmos.color = Color.white;
					Gizmos.DrawLine( irradiancePolygon.Vertices[0], irradiancePolygon.Vertices[irradiancePolygon.Vertices.Length-1] );
					Gizmos.DrawCube( irradiancePolygon.Vertices[0], Vector3.one * 0.01f );
				}
			}
		}
	}
	#endregion
}
