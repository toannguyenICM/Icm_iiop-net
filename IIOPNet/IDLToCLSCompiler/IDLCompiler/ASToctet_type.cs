/* Generated By:JJTree: Do not edit this line. ASToctet_type.cs */

using System;

namespace parser {

public class ASToctet_type : SimpleNode {
  public ASToctet_type(int id) : base(id) {
  }

  public ASToctet_type(IDLParser p, int id) : base(p, id) {
  }


  /** Accept the visitor. **/
  public override Object jjtAccept(IDLParserVisitor visitor, Object data) {
    return visitor.visit(this, data);
  }
  
  public override string GetIdentification() {      
    return "octet";      
  }    

}


}

